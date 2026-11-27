/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2020 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <algorithm>
#include <array>
#include <cstring>
#include <cstdlib>
#include <queue>
#include <string>
#include <unordered_map>

#include <fmt/format.h>

#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/filesystem/devices/host_path_device.h>
#include <rex/filesystem/devices/stfs_container_device.h>
#include <rex/string.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xam/content_device.h>
#include <rex/system/xam/content_manager.h>
#include <rex/system/xfile.h>
#include <rex/system/xobject.h>

REXCVAR_DEFINE_STRING(content_title_folders, "", "Runtime",
                      "Each title's content folder in the profile, as TITLEID=Folder pairs separated "
                      "by ';'. A title not named uses its id.");

namespace rex {
namespace system {
namespace xam {

static const char* kThumbnailFileName = "__thumbnail.png";

static const char* kGameUserContentDirName = "profile";

static const char* kGameContentHeaderDirName = "Headers";

static int content_device_id_ = 0;

// REFII SHADOW: A PACKAGE IS WRITTEN WHOLE OR NOT AT ALL. The guest opens a save package, writes
// its files in place and closes it (Fable 2: lhSaveManager_WritePackage, WriteHeroPackage and
// SaveGame_LoadFromDevice all end in XamContentClose, TU1 sub_82449BD8). A process that dies
// between the two - a guest exit(1) part-way through a write, a crash, power loss - used to leave
// a half-written file the next load read as the save. So a writable package is copied aside when
// it opens and the copy is dropped when it closes; a copy still there at the next open means the
// last session never closed the package, and the copy is put back before the guest sees it. A
// package created in that open had no earlier state: it is removed. The copies live beside the
// content types, <title folder>/refii-pending/<type>/<name>, where no listing looks.
static const char* kPendingDirName = "refii-pending";
static const char* kCreatedMarkerName = ".refii-created";

// REFII SHADOW: ONE PACKAGE LEFT OUT OF THE LISTINGS ON THIS THREAD. A title's own list is what the
// enumeration hands it (Fable 2's co-op picker pushes each entry straight into its vectors), so a
// package the host must keep off one list is left out here, for the listings made on the thread that
// set it and only until it is cleared (RefiiContentListingOmit). User content only: the device's
// marketplace listing is never touched.
static thread_local std::string t_listing_omit;

static std::filesystem::path PendingPath(const std::filesystem::path& package_path) {
  const auto type_dir = package_path.parent_path();
  return type_dir.parent_path() / kPendingDirName / type_dir.filename() / package_path.filename();
}

static void RestorePending(const std::filesystem::path& package_path) {
  std::error_code ec;
  const auto pending = PendingPath(package_path);
  if (!std::filesystem::exists(pending, ec)) {
    return;
  }
  const bool created = std::filesystem::exists(pending / kCreatedMarkerName, ec);
  std::filesystem::remove_all(package_path, ec);
  if (created) {
    std::filesystem::remove_all(pending, ec);
    REXSYS_WARN("Content package {} was created and never closed (the session ended part-way "
                "through writing it): removed",
                rex::path_to_utf8(package_path));
    return;
  }
  ec.clear();
  std::filesystem::rename(pending, package_path, ec);
  if (ec) {
    ec.clear();
    std::filesystem::copy(pending, package_path, std::filesystem::copy_options::recursive, ec);
    if (!ec) {
      std::filesystem::remove_all(pending, ec);
    }
  }
  if (ec) {
    REXSYS_ERROR("Content package {} was left open by the last session; restoring its copy "
                 "from {} failed: {}",
                 rex::path_to_utf8(package_path), rex::path_to_utf8(pending), ec.message());
    return;
  }
  REXSYS_WARN("Content package {} was left open by the last session (it ended part-way through "
              "writing it): restored to the state it opened in",
              rex::path_to_utf8(package_path));
}

static void BeginPending(const std::filesystem::path& package_path, bool created) {
  std::error_code ec;
  const auto pending = PendingPath(package_path);
  std::filesystem::remove_all(pending, ec);
  ec.clear();
  std::filesystem::create_directories(pending.parent_path(), ec);
  if (created) {
    std::filesystem::create_directories(pending, ec);
    if (FILE* marker = rex::filesystem::OpenFile(pending / kCreatedMarkerName, "wb")) {
      fclose(marker);
    }
  } else {
    std::filesystem::copy(package_path, pending, std::filesystem::copy_options::recursive, ec);
  }
  if (ec) {
    REXSYS_ERROR("Content package {}: its copy for a crash part-way through a write could not be "
                 "made ({}); this open is unprotected",
                 rex::path_to_utf8(package_path), ec.message());
    std::filesystem::remove_all(pending, ec);
  }
}

static void EndPending(const std::filesystem::path& package_path) {
  std::error_code ec;
  std::filesystem::remove_all(PendingPath(package_path), ec);
}

ContentPackage::ContentPackage(KernelState* kernel_state, const std::string_view root_name,
                               const XCONTENT_AGGREGATE_DATA& data,
                               const std::filesystem::path& package_path, bool read_only)
    : kernel_state_(kernel_state), root_name_(root_name), package_path_(package_path), license_(0) {
  device_path_ = fmt::format("\\Device\\Content\\{0}\\", ++content_device_id_);
  content_data_ = data;

  auto fs = kernel_state_->file_system();
  auto device = std::make_unique<rex::filesystem::HostPathDevice>(device_path_, package_path, read_only,
                                                                  /*allow_share_delete=*/true);
  device->Initialize();
  fs->RegisterDevice(std::move(device));
  fs->RegisterSymbolicLink(root_name_ + ":", device_path_);
}

ContentPackage::~ContentPackage() {
  auto fs = kernel_state_->file_system();
  fs->UnregisterSymbolicLink(root_name_ + ":");

  // REFII SHADOW: THE GUEST OWNS ITS HANDLES. The pristine close force-released every file
  // handle still open inside the package (CloseOpenedFilesFromContent) so the device could be
  // torn down safely. That steals handle NUMBERS: the freed slots are recycled at once (the
  // next save open's event took one), and when the guest later closes its own, now stale,
  // file handle it closes the recycled object instead - the event a deferred completion has
  // to signal - and the GameThread waits forever in XGetOverlappedResult (2026-09-03, save
  // load in the world-session ctor, event F8000254). The device's entries are what an open
  // XFile points at, so the device stays registered while any of its files is open; the
  // symbolic link is gone, so nothing new can reach it.
  const auto open_files =
      kernel_state_->object_table()->GetObjectsByType<XFile>(XObject::Type::File);
  size_t still_open = 0;
  for (const object_ref<XFile>& file : open_files) {
    if (rex::string::utf8_starts_with(file->entry()->absolute_path(), device_path_)) {
      ++still_open;
    }
  }
  if (still_open) {
    REXSYS_WARN("Content package '{}' closed with {} file(s) still open - device {} kept alive "
                "for them; the guest closes its own handles",
                root_name_, still_open, device_path_);
    return;
  }
  fs->UnregisterDevice(device_path_);
}

void ContentPackage::LoadPackageLicenseMask(const std::filesystem::path header_path) {
  if (!std::filesystem::exists(header_path)) {
    return;
  }

  auto file = rex::filesystem::OpenFile(header_path, "rb");
  if (!file) {
    return;
  }

  auto file_size = std::filesystem::file_size(header_path);
  if (file_size < sizeof(XCONTENT_AGGREGATE_DATA) + sizeof(license_)) {
    fclose(file);
    return;
  }

  fseek(file, sizeof(XCONTENT_AGGREGATE_DATA), SEEK_SET);
  fread(&license_, 1, sizeof(license_), file);
  fclose(file);
}

// REFII SHADOW: THE ROOT IS ONE PROFILE. The host states user_data_root as the profile's own
// folder and names each title's folder in it (content_title_folders), so the layout is
//
//   content_root/<title folder>/<content type>/<file name>/
//   content_root/<title folder>/Headers/<content type>/<file name>.header
//   content_root/<title folder>/profile/<setting id>
//
// and the XUID picks no folder. The guest never reads a package's XUID back: enumeration hands
// it XCONTENT_DATA without one, and XamContentGetCreator answers the live profile (F2 TU1
// sub_82449930 -> XamContentGetCreator, F3 0x83100908 copies 0x134 bytes and the title id only).
// Marketplace content (type 2) is the device's, every other type the profile's: a listing for
// the common owner (xuid 0) sees only the first, one for the signed-in xuid only the rest, so
// the enumerators' two calls never list a package twice.
ContentManager::ContentManager(KernelState* kernel_state, const std::filesystem::path& root_path)
    : kernel_state_(kernel_state), root_path_(root_path) {
  const std::string folders = REXCVAR_GET(content_title_folders);
  for (size_t pos = 0; pos < folders.size();) {
    size_t end = folders.find(';', pos);
    if (end == std::string::npos) {
      end = folders.size();
    }
    const std::string pair = folders.substr(pos, end - pos);
    const size_t eq = pair.find('=');
    if (eq != std::string::npos && eq > 0 && eq + 1 < pair.size()) {
      const uint32_t title = static_cast<uint32_t>(std::strtoul(pair.substr(0, eq).c_str(), nullptr, 16));
      const std::string folder = pair.substr(eq + 1);
      if (title != 0 && folder.find_first_of("/\\") == std::string::npos && folder != "." &&
          folder != "..") {
        title_folders_[title] = folder;
      }
    }
    pos = end + 1;
  }
}

ContentManager::~ContentManager() = default;

std::filesystem::path ContentManager::ResolveTitleRoot(uint64_t xuid, uint32_t title_id) const {
  (void)xuid;
  if (title_id == kCurrentlyRunningTitleId) {
    title_id = kernel_state_->title_id();
  }
  auto it = title_folders_.find(title_id);
  return root_path_ / (it != title_folders_.end() ? rex::to_path(it->second)
                                                  : rex::to_path(fmt::format("{:08X}", title_id)));
}

bool ContentManager::IsOtherTitle(uint32_t title_id) const {
  return title_id != kCurrentlyRunningTitleId && title_id != kernel_state_->title_id();
}

std::filesystem::path ContentManager::ResolvePackageRoot(uint64_t xuid, XContentType content_type,
                                                         uint32_t title_id) {
  // content_root/<title folder>/content_type/
  return ResolveTitleRoot(xuid, title_id) / fmt::format("{:08X}", uint32_t(content_type));
}

std::filesystem::path ContentManager::ResolvePackagePath(uint64_t xuid,
                                                         const XCONTENT_AGGREGATE_DATA& data) {
  uint64_t used_xuid = (data.xuid != uint64_t(-1) && data.xuid != 0) ? uint64_t(data.xuid) : xuid;

  // DLCs are stored in common directory
  if (data.content_type == XContentType::kMarketplaceContent) {
    used_xuid = 0;
  }

  // Content path:
  // content_root/xuid/title_id/content_type/data_file_name/
  auto package_root = ResolvePackageRoot(used_xuid, data.content_type, data.title_id);
  return package_root / rex::to_path(data.file_name());
}

std::filesystem::path ContentManager::ResolvePackageHeaderPath(const std::string_view file_name,
                                                               uint64_t xuid, uint32_t title_id,
                                                               XContentType content_type) const {
  if (title_id == kCurrentlyRunningTitleId) {
    title_id = kernel_state_->title_id();
  }

  std::string final_name = std::string(file_name) + ".header";

  // content_root/<title folder>/Headers/content_type/filename.header
  return ResolveTitleRoot(xuid, title_id) / kGameContentHeaderDirName /
         fmt::format("{:08X}", uint32_t(content_type)) / rex::to_path(final_name);
}

std::vector<XCONTENT_AGGREGATE_DATA> ContentManager::ListContent(uint32_t device_id, uint64_t xuid,
                                                                 XContentType content_type,
                                                                 uint32_t title_id) {
  std::vector<XCONTENT_AGGREGATE_DATA> result;

  if (title_id == kCurrentlyRunningTitleId) {
    title_id = kernel_state_->title_id();
  }

  // One owner per content type: the device (xuid 0) owns marketplace content, the profile the rest.
  if ((xuid == 0) != (content_type == XContentType::kMarketplaceContent)) {
    return result;
  }

  // Search path:
  // content_root/<title folder>/type_name/*
  auto package_root = ResolvePackageRoot(xuid, content_type, title_id);
  auto file_infos = rex::filesystem::ListFiles(package_root);

  // NEWEST FIRST. The title takes this list in the order it is handed and never re-sorts it
  // (`sub_82443048` pushes each entry straight into its own vector), so the order IS the game's
  // notion of "latest": the Load Game fan opens on it, and Refii loads index 0 when a co-op
  // joiner accepts from the title with no hero standing. A save is a directory whose package
  // rewrite bumps its write time, which is the right key; the sort is stable so equal times
  // keep directory order.
  std::stable_sort(file_infos.begin(), file_infos.end(),
                   [](const rex::filesystem::FileInfo& a, const rex::filesystem::FileInfo& b) {
                     return a.write_timestamp > b.write_timestamp;
                   });

  for (const auto& file_info : file_infos) {
    if (file_info.type != rex::filesystem::FileInfo::Type::kDirectory) {
      // Directories only.
      continue;
    }
    if (content_type != XContentType::kMarketplaceContent && !t_listing_omit.empty() &&
        rex::path_to_utf8(file_info.name) == t_listing_omit) {
      REXSYS_INFO("Content listing: '{}' left out of this listing (the host's omission)", t_listing_omit);
      continue;
    }

    XCONTENT_AGGREGATE_DATA content_data;
    if (XSUCCEEDED(ReadContentHeaderFile(rex::path_to_utf8(file_info.name), xuid, title_id,
                                         content_type, content_data))) {
      result.emplace_back(std::move(content_data));
    } else {
      content_data.device_id = device_id;
      content_data.content_type = content_type;
      content_data.set_display_name(rex::path_to_utf16(file_info.name));
      content_data.set_file_name(rex::path_to_utf8(file_info.name));
      content_data.title_id = title_id;
      content_data.xuid = xuid;
      result.emplace_back(std::move(content_data));
    }
  }

  return result;
}

std::unique_ptr<ContentPackage> ContentManager::ResolvePackage(
    const std::string_view root_name, uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data) {
  auto package_path = ResolvePackagePath(xuid, data);
  if (!std::filesystem::exists(package_path)) {
    return nullptr;
  }
  auto package = std::make_unique<ContentPackage>(kernel_state_, root_name, data, package_path);
  return package;
}

bool ContentManager::ContentExists(uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data) {
  auto path = ResolvePackagePath(xuid, data);
  return std::filesystem::exists(path);
}

X_RESULT ContentManager::WriteContentHeaderFile(uint64_t xuid, XCONTENT_AGGREGATE_DATA data,
                                                uint32_t license_mask) {
  if (IsOtherTitle(data.title_id)) {
    return X_ERROR_ACCESS_DENIED;
  }
  if (data.title_id == uint32_t(-1)) {
    data.title_id = kernel_state_->title_id();
  }
  if (data.xuid == uint64_t(-1)) {
    data.xuid = xuid;
  }
  uint64_t used_xuid = (data.xuid != uint64_t(-1) && data.xuid != 0) ? uint64_t(data.xuid) : xuid;

  auto header_path =
      ResolvePackageHeaderPath(data.file_name(), used_xuid, data.title_id, data.content_type);
  auto parent_path = header_path.parent_path();

  if (!std::filesystem::exists(parent_path)) {
    if (!std::filesystem::create_directories(parent_path)) {
      return X_ERROR_ACCESS_DENIED;
    }
  }

  rex::filesystem::CreateEmptyFile(header_path);

  auto file = rex::filesystem::OpenFile(header_path, "wb");
  if (!file) {
    return X_ERROR_FILE_NOT_FOUND;
  }
  fwrite(&data, 1, sizeof(XCONTENT_AGGREGATE_DATA), file);
  if (license_mask != 0) {
    fwrite(&license_mask, 1, sizeof(license_mask), file);
  }
  fclose(file);
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::ReadContentHeaderFile(const std::string_view file_name, uint64_t xuid,
                                               uint32_t title_id, XContentType content_type,
                                               XCONTENT_AGGREGATE_DATA& data) const {
  auto header_file_path = ResolvePackageHeaderPath(file_name, xuid, title_id, content_type);
  constexpr uint32_t header_size = sizeof(XCONTENT_AGGREGATE_DATA);

  if (!std::filesystem::exists(header_file_path)) {
    return X_ERROR_FILE_NOT_FOUND;
  }

  auto file = rex::filesystem::OpenFile(header_file_path, "rb");
  if (!file) {
    return X_ERROR_FILE_NOT_FOUND;
  }

  auto file_size = std::filesystem::file_size(header_file_path);
  if (file_size < header_size) {
    fclose(file);
    return X_ERROR_FILE_NOT_FOUND;
  }

  std::array<uint8_t, header_size> buffer;
  size_t result = fread(buffer.data(), 1, header_size, file);
  fclose(file);

  if (result != header_size) {
    return X_ERROR_FILE_NOT_FOUND;
  }

  std::memcpy(&data, buffer.data(), buffer.size());
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::CreateContent(const std::string_view root_name, uint64_t xuid,
                                       const XCONTENT_AGGREGATE_DATA& data) {
  if (IsOtherTitle(data.title_id)) {
    return X_ERROR_ACCESS_DENIED;
  }
  {
    auto global_lock = global_critical_region_.Acquire();
    if (open_packages_.count(string::string_key_case(root_name))) {
      return X_ERROR_ALREADY_EXISTS;
    }
  }

  auto package_path = ResolvePackagePath(xuid, data);
  if (std::filesystem::exists(package_path)) {
    return X_ERROR_ALREADY_EXISTS;
  }
  if (!std::filesystem::create_directories(package_path)) {
    return X_ERROR_ACCESS_DENIED;
  }
  BeginPending(package_path, /*created=*/true);
  auto package = ResolvePackage(root_name, xuid, data);
  assert_not_null(package);

  {
    auto global_lock = global_critical_region_.Acquire();
    if (open_packages_.count(string::string_key_case(root_name))) {
      return X_ERROR_ALREADY_EXISTS;
    }
    open_packages_.insert({string::string_key_case::create(root_name), package.release()});
  }
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::OpenContent(const std::string_view root_name, uint64_t xuid,
                                     const XCONTENT_AGGREGATE_DATA& data,
                                     uint32_t& content_license) {
  {
    auto global_lock = global_critical_region_.Acquire();
    if (open_packages_.count(string::string_key_case(root_name))) {
      return X_ERROR_ALREADY_EXISTS;
    }
  }

  auto package_path = ResolvePackagePath(xuid, data);
  const bool read_only = IsOtherTitle(data.title_id);
  if (!read_only) {
    RestorePending(package_path);
  }
  if (!std::filesystem::exists(package_path)) {
    return X_ERROR_FILE_NOT_FOUND;
  }
  if (!read_only) {
    BeginPending(package_path, /*created=*/false);
  }
  // Another title's content mounts read-only: open-existing is all XAM grants it (F2 TU1
  // lhXam_XamContentCreateInternal 0x82cc4138: an alternate title with a disposition other
  // than 3 returns 5; F3 sub_822FA998 opens Fable 2's packages with 3 and reads HeroSave.bin).
  auto package = std::make_unique<ContentPackage>(kernel_state_, root_name, data, package_path,
                                                  IsOtherTitle(data.title_id));
  // The record's own title: a cross-title open reads that title's header, not the running one's.
  package->LoadPackageLicenseMask(
      ResolvePackageHeaderPath(data.file_name(), xuid, data.title_id, data.content_type));
  content_license = package->GetPackageLicense();

  {
    auto global_lock = global_critical_region_.Acquire();
    if (open_packages_.count(string::string_key_case(root_name))) {
      return X_ERROR_ALREADY_EXISTS;
    }
    open_packages_.insert({string::string_key_case::create(root_name), package.release()});
  }
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::CloseContent(const std::string_view root_name) {
  ContentPackage* package = nullptr;
  {
    auto global_lock = global_critical_region_.Acquire();
    // Some games use different casing between Create and Close (e.g. "save" vs "SAVE")
    auto it = open_packages_.find(string::string_key_case(root_name));
    if (it == open_packages_.end()) {
      return X_ERROR_FILE_NOT_FOUND;
    }
    package = DetachPackage(it);
  }
  const auto package_path = package->package_path();
  delete package;
  EndPending(package_path);
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::GetContentThumbnail(uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data,
                                             std::vector<uint8_t>* buffer) {
  auto global_lock = global_critical_region_.Acquire();
  auto package_path = ResolvePackagePath(xuid, data);
  auto thumb_path = package_path / kThumbnailFileName;
  if (std::filesystem::exists(thumb_path)) {
    auto file = rex::filesystem::OpenFile(thumb_path, "rb");
    fseek(file, 0, SEEK_END);
    size_t file_len = ftell(file);
    fseek(file, 0, SEEK_SET);
    buffer->resize(file_len);
    fread(const_cast<uint8_t*>(buffer->data()), 1, buffer->size(), file);
    fclose(file);
    return X_ERROR_SUCCESS;
  } else {
    return X_ERROR_FILE_NOT_FOUND;
  }
}

X_RESULT ContentManager::SetContentThumbnail(uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data,
                                             std::vector<uint8_t> buffer) {
  if (IsOtherTitle(data.title_id)) {
    return X_ERROR_ACCESS_DENIED;
  }
  auto global_lock = global_critical_region_.Acquire();
  auto package_path = ResolvePackagePath(xuid, data);
  std::filesystem::create_directories(package_path);
  if (std::filesystem::exists(package_path)) {
    auto thumb_path = package_path / kThumbnailFileName;
    auto file = rex::filesystem::OpenFile(thumb_path, "wb");
    fwrite(buffer.data(), 1, buffer.size(), file);
    fclose(file);
    return X_ERROR_SUCCESS;
  } else {
    return X_ERROR_FILE_NOT_FOUND;
  }
}

X_RESULT ContentManager::DeleteContent(uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data) {
  if (IsOtherTitle(data.title_id)) {
    return X_ERROR_ACCESS_DENIED;
  }
  auto global_lock = global_critical_region_.Acquire();

  if (IsContentOpen(data)) {
    // TODO(Gliniak): Get real error code for this case.
    return X_ERROR_ACCESS_DENIED;
  }

  auto package_path = ResolvePackagePath(xuid, data);
  EndPending(package_path);
  std::error_code ec;
  auto dir_removed = std::filesystem::remove_all(package_path, ec);
  if (ec) {
    return X_ERROR_ACCESS_DENIED;
  }

  uint64_t used_xuid = (data.xuid != uint64_t(-1) && data.xuid != 0) ? uint64_t(data.xuid) : xuid;
  auto header_path =
      ResolvePackageHeaderPath(data.file_name(), used_xuid, data.title_id, data.content_type);
  std::error_code ec2;
  bool header_removed = std::filesystem::remove(header_path, ec2);

  if (dir_removed > 0 || header_removed) {
    return X_ERROR_SUCCESS;
  }
  return X_ERROR_FILE_NOT_FOUND;
}

X_RESULT ContentManager::UnmountContent(uint64_t xuid, const XCONTENT_AGGREGATE_DATA& data) {
  ContentPackage* package = nullptr;
  {
    auto global_lock = global_critical_region_.Acquire();
    auto it = FindOpenPackageByData(data);
    if (it == open_packages_.end()) {
      return X_ERROR_FILE_NOT_FOUND;
    }
    package = DetachPackage(it);
  }
  const auto package_path = package->package_path();
  delete package;
  EndPending(package_path);
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::UnmountAndDeleteContent(uint64_t xuid,
                                                 const XCONTENT_AGGREGATE_DATA& data) {
  if (IsOtherTitle(data.title_id)) {
    return X_ERROR_ACCESS_DENIED;
  }
  // Unmount phase: tolerant of not-mounted state
  ContentPackage* package = nullptr;
  {
    auto global_lock = global_critical_region_.Acquire();
    auto it = FindOpenPackageByData(data);
    if (it != open_packages_.end()) {
      package = DetachPackage(it);
    }
  }
  delete package;

  // Delete phase: remove package directory and .header file
  auto package_path = ResolvePackagePath(xuid, data);
  EndPending(package_path);

  uint64_t used_xuid = (data.xuid != uint64_t(-1) && data.xuid != 0) ? uint64_t(data.xuid) : xuid;
  auto header_path =
      ResolvePackageHeaderPath(data.file_name(), used_xuid, data.title_id, data.content_type);

  std::error_code ec;
  auto dir_removed = std::filesystem::remove_all(package_path, ec);
  if (ec) {
    return X_ERROR_ACCESS_DENIED;
  }

  std::error_code ec2;
  bool header_removed = std::filesystem::remove(header_path, ec2);

  if (dir_removed > 0 || header_removed) {
    return X_ERROR_SUCCESS;
  }
  return X_ERROR_FILE_NOT_FOUND;
}

std::filesystem::path ContentManager::ResolveGameUserContentPath() {
  // content_root/<title folder>/profile: the root is the profile, so the profile's name picks
  // no folder (it changes with RexNet on or off; the settings must not).
  return ResolveTitleRoot(kernel_state_->user_profile()->xuid(), kCurrentlyRunningTitleId) /
         kGameUserContentDirName;
}

std::unordered_map<string::string_key_case, ContentPackage*,
                   string::string_key_case::Hash>::iterator
ContentManager::FindOpenPackageByData(const XCONTENT_AGGREGATE_DATA& data) {
  // Resolve kCurrentlyRunningTitleId so both sides compare actual title IDs.
  uint32_t query_title = data.title_id;
  if (query_title == kCurrentlyRunningTitleId) {
    query_title = kernel_state_->title_id();
  }

  for (auto it = open_packages_.begin(); it != open_packages_.end(); ++it) {
    const auto& pkg = it->second->GetPackageContentData();

    uint32_t pkg_title = pkg.title_id;
    if (pkg_title == kCurrentlyRunningTitleId) {
      pkg_title = kernel_state_->title_id();
    }

    // Match on content_type + file_name + resolved title_id.
    // device_id is a virtual storage selector, not a content identifier.
    if (data.content_type == pkg.content_type && data.file_name() == pkg.file_name() &&
        query_title == pkg_title) {
      return it;
    }
  }
  return open_packages_.end();
}

ContentPackage* ContentManager::DetachPackage(
    std::unordered_map<string::string_key_case, ContentPackage*,
                       string::string_key_case::Hash>::iterator it) {
  // REFII SHADOW: no CloseOpenedFilesFromContent here - see ~ContentPackage.
  ContentPackage* package = it->second;
  open_packages_.erase(it);
  return package;
}

bool ContentManager::IsContentOpen(const XCONTENT_AGGREGATE_DATA& data) const {
  return std::any_of(open_packages_.cbegin(), open_packages_.cend(), [&data](const auto& content) {
    return data == content.second->GetPackageContentData();
  });
}

std::filesystem::path ContentManager::GetOpenPackagePath(const std::string_view root_name) const {
  auto it = open_packages_.find(string::string_key_case(root_name));
  if (it == open_packages_.end()) {
    return {};
  }
  return it->second->package_path();
}

void ContentManager::CloseOpenedFilesFromContent(const std::string_view root_name) {
  // TODO(Gliniak): Cleanup this code to care only about handles
  // related to provided content
  const std::vector<object_ref<XFile>> all_files_handles =
      kernel_state_->object_table()->GetObjectsByType<XFile>(XObject::Type::File);

  std::string resolved_path = "";
  kernel_state_->file_system()->FindSymbolicLink(std::string(root_name) + ':', resolved_path);

  for (const object_ref<XFile>& file : all_files_handles) {
    std::string file_path = file->entry()->absolute_path();
    bool is_file_inside_content = rex::string::utf8_starts_with(file_path, resolved_path);

    if (is_file_inside_content) {
      file->ReleaseHandle();
    }
  }
}

static X_RESULT ExtractEntry(rex::filesystem::Entry* entry,
                             const std::filesystem::path& base_path) {
  auto dest_path = base_path / rex::to_path(rex::string::utf8_fix_path_separators(entry->path()));

  if (entry->attributes() & rex::filesystem::kFileAttributeDirectory) {
    std::error_code ec;
    std::filesystem::create_directories(dest_path, ec);
    if (ec) {
      return X_ERROR_ACCESS_DENIED;
    }
    return X_ERROR_SUCCESS;
  }

  // Ensure parent directory exists
  std::error_code ec;
  std::filesystem::create_directories(dest_path.parent_path(), ec);

  rex::filesystem::File* in_file = nullptr;
  X_STATUS status = entry->Open(rex::filesystem::FileAccess::kFileReadData, &in_file);
  if (status != X_STATUS_SUCCESS) {
    return X_ERROR_ACCESS_DENIED;
  }

  auto out_file = rex::filesystem::OpenFile(dest_path, "wb");
  if (!out_file) {
    in_file->Destroy();
    return X_ERROR_ACCESS_DENIED;
  }

  constexpr size_t kBufferSize = 4 * 1024 * 1024;  // 4 MiB
  auto buffer = std::make_unique<uint8_t[]>(kBufferSize);
  size_t remaining = entry->size();
  size_t offset = 0;

  while (remaining > 0) {
    size_t bytes_read = 0;
    size_t to_read = std::min(remaining, kBufferSize);
    in_file->ReadSync(std::span<uint8_t>(buffer.get(), to_read), offset, &bytes_read);
    if (bytes_read == 0) {
      break;
    }
    fwrite(buffer.get(), 1, bytes_read, out_file);
    offset += bytes_read;
    remaining -= bytes_read;
  }

  fclose(out_file);
  in_file->Destroy();
  return X_ERROR_SUCCESS;
}

X_RESULT ContentManager::InstallContent(const std::filesystem::path& package_path) {
  if (!std::filesystem::exists(package_path)) {
    return X_ERROR_FILE_NOT_FOUND;
  }

  // Mount the STFS package as a virtual filesystem device
  auto device = std::make_unique<rex::filesystem::StfsContainerDevice>("", package_path);
  if (!device->Initialize()) {
    return X_ERROR_ACCESS_DENIED;
  }

  // Derive install destination:
  // root_path_/0000000000000000/{title_id}/00000002/{filename}/
  auto file_name = rex::path_to_utf8(package_path.filename());

  XCONTENT_AGGREGATE_DATA content_data;
  content_data.device_id = static_cast<uint32_t>(DummyDeviceId::HDD);
  content_data.content_type = XContentType::kMarketplaceContent;
  content_data.title_id = kernel_state_->title_id();
  content_data.xuid = 0;
  content_data.set_file_name(file_name);

  // Read display name from STFS metadata
  auto display_name = device->header().metadata.display_name(rex::system::XLanguage::kEnglish);
  if (!display_name.empty()) {
    content_data.set_display_name(display_name);
  } else {
    content_data.set_display_name(rex::path_to_utf16(package_path.filename()));
  }

  auto install_path = ResolvePackagePath(0, content_data);

  // Create destination directory
  std::error_code ec;
  std::filesystem::create_directories(install_path, ec);
  if (ec) {
    return X_ERROR_ACCESS_DENIED;
  }

  // Extract all files breadth-first
  auto* root = device->ResolvePath("");
  if (!root) {
    return X_ERROR_ACCESS_DENIED;
  }

  std::queue<rex::filesystem::Entry*> queue;
  queue.push(root);

  while (!queue.empty()) {
    auto* entry = queue.front();
    queue.pop();

    for (auto& child : entry->children()) {
      queue.push(child.get());
    }

    auto result = ExtractEntry(entry, install_path);
    if (result != X_ERROR_SUCCESS) {
      return result;
    }
  }

  // Compute license mask from STFS header licenses
  uint32_t license_mask = 0;
  for (size_t i = 0; i < 0x10; i++) {
    if (device->header().header.licenses[i].license_flags) {
      license_mask |= device->header().header.licenses[i].license_bits;
    }
  }

  // Write .header file
  return WriteContentHeaderFile(0, content_data, license_mask);
}

}  // namespace xam
}  // namespace system
}  // namespace rex

// REFII SHADOW: the package named `file_name` is left out of every user-content listing this thread
// makes until it is called again with null or "" (ContentManager::ListContent).
extern "C" void RefiiContentListingOmit(const char* file_name) {
  rex::system::xam::t_listing_omit = file_name != nullptr ? file_name : "";
}
