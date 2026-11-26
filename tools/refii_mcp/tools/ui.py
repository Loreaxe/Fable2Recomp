"""What the guest states about its UI: the screen, the rows, the front end, the live elements."""
from ..channel import ask
from ..registry import tool


@tool(
    "refii_state",
    "What the RUNNING game states about its UI right now: which in-game screen is up "
    "(None/31 means no menu), the pause gate's screen id, modal mode, current tab, which "
    "screen the menu spawn capture belongs to, and the world-map region sample.",
)
def state(_args):
    return ask({"op": "state"})


@tool(
    "refii_rows",
    "The menu spawn capture as the guest last stated it — every row's name, parent, resolved "
    "label, id, event type and rating, plus the generation and the screen the population "
    "belongs to.",
)
def rows(_args):
    return ask({"op": "rows"})


@tool(
    "refii_frontend",
    "The FRONT END's state — title vs main menu, its state word (title: 9 legal, 0 idle, 8 the "
    "attract movie, 10/11 starting, 12 a warning), whether A is armed, the "
    "cursor row, and the scene slide (slide 1 on the main menu is PressStart, before the row "
    "list is up). `refii_state` cannot see this: the front end is a different GUI object from "
    "the in-game screen manager, so a run parked on the title or the gender card shows only "
    "'no manager' there.",
)
def frontend(_args):
    return ask({"op": "frontend"})


@tool(
    "refii_elements",
    "The RESOLVED state of every live element whose name contains `match`, across every book "
    "that is up: active, drawn, opacity and node position. This is where a page bug actually "
    "lives — a channel write that is bound but ineffective looks identical in the logs and "
    "different here. Case-sensitive substring match on the element name.",
    {
        "match": {"type": "string", "description": "Substring of the element name. Required."},
        "limit": {"type": "integer", "description": "Max lines per book (default 40)."},
    },
    ["match"],
)
def elements(args):
    match = str(args.get("match", ""))
    if not match:
        return {"ok": False, "error": "elements needs a match — it will not dump every book"}
    return ask({"op": "elements", "match": match, "limit": args.get("limit", 40)}, timeout=15.0)
