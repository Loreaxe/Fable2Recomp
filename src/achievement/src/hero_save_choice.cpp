#include <achievement/hero_save_choice.h>

#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

// One question per joining player at a time, oldest first. The asker is the guest thread running the
// join; it waits on `g_answered` until the host answers, or — while no host has taken the question —
// until `present_ms` passes. The host takes the question by copying it (Front) and answers by id.
namespace {

struct Question {
  std::uint32_t id = 0;
  std::uint32_t user = 0;
  std::uint32_t listed = 0;
  std::vector<std::string> saves;
  bool taken = false;
  bool answered = false;
  std::int32_t choice = REFII_HERO_CHOICE_NO_ANSWER;
};

std::mutex g_mutex;
std::condition_variable g_answered;
std::deque<std::shared_ptr<Question>> g_questions;
std::uint32_t g_next = 1;

constexpr char kSeparator = '\x1F';

void Withdraw(const std::shared_ptr<Question>& question) {
  for (auto it = g_questions.begin(); it != g_questions.end(); ++it) {
    if (*it == question) {
      g_questions.erase(it);
      return;
    }
  }
}

}  // namespace

extern "C" std::int32_t RefiiHeroSaveChoiceAsk(std::uint32_t user, const char* names, std::uint32_t count,
                                               std::uint32_t listed, std::uint32_t present_ms) {
  auto question = std::make_shared<Question>();
  question->user = user;
  question->listed = listed;
  const char* at = names;
  for (std::uint32_t i = 0; i < count && at != nullptr; ++i) {
    const char* end = std::strchr(at, kSeparator);
    if (end == nullptr) {
      question->saves.emplace_back(at);
      break;
    }
    question->saves.emplace_back(at, static_cast<size_t>(end - at));
    at = end + 1;
  }
  if (question->saves.empty()) {
    return REFII_HERO_CHOICE_NO_ANSWER;
  }

  std::unique_lock<std::mutex> lock(g_mutex);
  question->id = g_next++;
  g_questions.push_back(question);
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(present_ms);
  while (!question->answered) {
    if (question->taken) {
      g_answered.wait(lock);
    } else if (g_answered.wait_until(lock, deadline) == std::cv_status::timeout && !question->taken &&
               !question->answered) {
      Withdraw(question);
      return REFII_HERO_CHOICE_NO_ANSWER;
    }
  }
  return question->choice;
}

extern "C" std::uint32_t RefiiHeroSaveChoiceFront(char* out, std::uint32_t capacity) {
  std::string record;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_questions.empty()) {
      return 0u;
    }
    Question& question = *g_questions.front();
    record = std::to_string(question.id) + kSeparator + std::to_string(question.user) + kSeparator +
             std::to_string(question.listed) + kSeparator + std::to_string(question.saves.size());
    for (const std::string& save : question.saves) {
      record += kSeparator;
      record += save;
    }
    if (out != nullptr && capacity > record.size()) {
      question.taken = true;
    }
  }
  if (out == nullptr || capacity <= record.size()) {
    return static_cast<std::uint32_t>(record.size());
  }
  std::memcpy(out, record.data(), record.size());
  out[record.size()] = '\0';
  g_answered.notify_all();
  return static_cast<std::uint32_t>(record.size());
}

extern "C" std::int32_t RefiiHeroSaveChoiceAnswer(std::uint32_t id, std::int32_t choice) {
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    std::shared_ptr<Question> question;
    for (const auto& waiting : g_questions) {
      if (waiting->id == id) {
        question = waiting;
        break;
      }
    }
    if (question == nullptr ||
        (choice != REFII_HERO_CHOICE_NEW_HERO && choice != REFII_HERO_CHOICE_CANCEL &&
         (choice < 0 || static_cast<std::size_t>(choice) >= question->saves.size()))) {
      return 0;
    }
    question->choice = choice;
    question->answered = true;
    Withdraw(question);
  }
  g_answered.notify_all();
  return 1;
}
