"""`refii_pads` argument checks and the request they make; no game needed."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from refii_mcp.tools.drive import pads_payload


class PadsPayloadTests(unittest.TestCase):
    def test_list_is_the_default(self):
        self.assertEqual(pads_payload({}), {"op": "pads", "do": "list"})
        self.assertEqual(pads_payload({"do": "List"}), {"op": "pads", "do": "list"})

    def test_connect_takes_the_seat_and_the_family(self):
        self.assertEqual(pads_payload({"do": "connect"}), {"op": "pads", "do": "connect"})
        self.assertEqual(pads_payload({"do": "connect", "pad": 2, "family": "Nintendo"}),
                         {"op": "pads", "do": "connect", "pad": 2, "family": "nintendo"})

    def test_disconnect(self):
        self.assertEqual(pads_payload({"do": "disconnect"}), {"op": "pads", "do": "disconnect"})

    def test_refusals(self):
        for bad in ({"do": "plug"}, {"do": "connect", "pad": 4}, {"do": "connect", "pad": -1},
                    {"do": "connect", "family": "sega"}, {"do": "list", "pad": 1},
                    {"do": "disconnect", "family": "xbox"}, {"pad": 1}):
            self.assertFalse(pads_payload(bad).get("ok", True), bad)


if __name__ == "__main__":
    unittest.main()
