// c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/joiner_shop_statement_test.cpp && ./a.out
#include "core/joiner_shop_statement.h"

#include <cstdio>

using namespace refii::coop;

static_assert(QuestionCloser(19u, 0x2Eu) == 1 && QuestionCloser(19u, 0x2Fu) == 2 && QuestionCloser(19u, 36u) == 0,
              "the worn item's sale closes by yes / no only");
static_assert(QuestionCloser(17u, 0x2Eu) == 1 && QuestionCloser(17u, 0x2Fu) == 2, "the tattoo removal likewise");
static_assert(QuestionCloser(20u, 36u) == 1 && QuestionCloser(20u, 75u) == 1 && QuestionCloser(20u, 0x2Fu) == 0,
              "buy-and-wear closes after either buy");
static_assert(QuestionCloser(16u, 36u) == 0, "a plain box is not a question");
static_assert(AnswerBox(AnswerValue(20u, 75u)) == 20u && AnswerCode(AnswerValue(20u, 75u)) == 75u, "answer round trip");
static_assert(InstallsAfterOwn(true, 1u) && !InstallsAfterOwn(true, 2u) && !InstallsAfterOwn(true, 3u) &&
                  !InstallsAfterOwn(false, 1u),
              "the list after the joiner's own transaction goes in once no box is up");
static_assert(InstallsPopulation(true, 301u, 1u) && !InstallsPopulation(true, 30u, 1u) &&
                  !InstallsPopulation(true, 301u, 2u) && !InstallsPopulation(false, 301u, 1u),
              "a newer list goes in on a header with no box up");

int main() {
  ShopStatement s;
  s.session = 7u;
  s.keeper = 0x00000001C0FFEE42ull;
  s.population = 3u;
  s.flags = kShopRepopulate;
  ShopRow folder;
  folder.name = "BuyItems";
  folder.parent = "Root";
  folder.label = "Buy Items";
  folder.event_type = 301;
  s.rows.push_back(folder);
  ShopRow item;
  item.name = "Buy\x1F" "Items";  // a separator inside a field is dropped, never splits it
  item.parent = "BuyItems";
  item.label = "Apple \xC3\xA9";
  item.icon = "icon";
  item.id = 41u;
  item.event_type = 30;
  item.rating = -1;
  item.emphasis = 2;
  item.item_flags = 5u;
  item.record = 0xDEADBEEFu;
  item.count = 3u;
  item.category = 5u;
  s.rows.push_back(item);
  s.prices.emplace_back(41u, "$12");
  s.modals.push_back(ShopModal{4u, 2u, 0x15u, "Item purchased: Apple"});
  const std::string wire = Encode(s);
  const auto back = Decode(wire);
  int bad = 0;
  const auto check = [&](bool ok, const char* what) {
    if (!ok) {
      std::printf("FAIL %s\n", what);
      ++bad;
    }
  };
  check(back.has_value(), "decodes");
  if (back) {
    check(back->session == 7u && back->keeper == s.keeper && back->population == 3u && back->flags == kShopRepopulate,
          "head");
    check(back->rows.size() == 2u, "rows");
    if (back->rows.size() == 2u) {
      const ShopRow& r = back->rows[1];
      check(r.name == "BuyItems" && r.parent == "BuyItems" && r.label == "Apple \xC3\xA9" && r.icon == "icon", "texts");
      check(r.id == 41u && r.event_type == 30 && r.rating == -1 && r.emphasis == 2 && r.item_flags == 5u &&
                r.record == 0xDEADBEEFu && r.count == 3u && r.category == 5u,
            "numbers");
      check(back->rows[0].event_type == 301 && back->rows[0].id == 0u, "folder");
    }
    check(back->prices.size() == 1u && back->prices[0].first == 41u && back->prices[0].second == "$12", "price");
    check(back->modals.size() == 1u && back->modals[0].serial == 4u && back->modals[0].kind == 2u &&
              back->modals[0].id == 0x15u && back->modals[0].text == "Item purchased: Apple",
          "modal");
  }
  check(!Decode("").has_value() && !Decode("X\x1F" "1").has_value(), "a bad head is nothing");
  ShopStatement empty;
  check(Decode(Encode(empty)).has_value() && Decode(Encode(empty))->rows.empty(), "an empty list");
  if (bad == 0) {
    std::printf("ok\n");
  }
  return bad;
}
