#pragma once

#include <cstdint>

// THE SAVE A JOINING PLAYER'S HERO COMES FROM, chosen by that player. The guest's drop-in join walks
// the profile's saves and adopts the first one its own qualify step passes (Fable 3 sub_82459A80 /
// disc sub_8243A190); the joining player chooses instead, from the saves that step passes, or a new
// hero (the guest's own no-save path). The guest thread asks and WAITS here, as it waits on any
// system UI the console shows over a join; the host shows the question and answers it from its own
// thread. The choice decides the character the joining user's achievements belong to
// (achievement_runtime.h: BindUser at the adoption that follows), which is why it lives in this seam.
//
//   guest join (hooks)  -> RefiiHeroSaveChoiceAsk    (blocks the guest thread until answered)
//   host bridge         -> RefiiHeroSaveChoiceFront / RefiiHeroSaveChoiceAnswer
//                          (RefiiHostGetHeroSaveChoice / RefiiHostAnswerHeroSaveChoice)
//
// A question no host takes within `present_ms` of being asked is answered "no answer": a guest whose
// shell shows nothing is never held. C linkage: the title's hooks declare these and resolve them
// through rex::runtime, as they do RefiiAchievementBindUser.

/// RefiiHeroSaveChoiceAsk's answer: the joining player wants a new hero (the guest's no-save path).
#define REFII_HERO_CHOICE_NEW_HERO (-1)
/// RefiiHeroSaveChoiceAsk's answer: no host took the question; the guest goes its own way.
#define REFII_HERO_CHOICE_NO_ANSWER (-2)
/// RefiiHeroSaveChoiceAsk's answer: the joining player does not join after all — the guest's own cancel of
/// the join (its sign-in warning's Cancel), so no hero is made and no save adopted.
#define REFII_HERO_CHOICE_CANCEL (-3)

extern "C" {

/// Asks the host which of `count` saves (`names`, each name followed by a 0x1F, in the guest's own order
/// of preference) user `user`'s hero comes from; `listed` is how many saves the profile holds. Returns
/// the index chosen, REFII_HERO_CHOICE_NEW_HERO, REFII_HERO_CHOICE_CANCEL or REFII_HERO_CHOICE_NO_ANSWER. Blocks
/// until then.
std::int32_t RefiiHeroSaveChoiceAsk(std::uint32_t user, const char* names, std::uint32_t count, std::uint32_t listed,
                                    std::uint32_t present_ms);

/// The question that waits, fields split by 0x1F: id, user, listed, count, then each save's name. Returns
/// the record's length (without the NUL), 0 when none waits; copies it when `capacity` holds it and the
/// NUL. A copy taken is the question taken: the asker then waits for the answer however long it takes.
std::uint32_t RefiiHeroSaveChoiceFront(char* out, std::uint32_t capacity);

/// The host's answer to question `id`: a save's index, REFII_HERO_CHOICE_NEW_HERO or REFII_HERO_CHOICE_CANCEL. 1 when it was
/// taken, 0 when no such question waits or the index is out of range.
std::int32_t RefiiHeroSaveChoiceAnswer(std::uint32_t id, std::int32_t choice);

}  // extern "C"
