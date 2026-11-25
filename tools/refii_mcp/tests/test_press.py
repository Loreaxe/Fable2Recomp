"""`refii_press` argument checks and the request they make, both modes; no game needed."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from refii_mcp.tools.drive import press_payload


class PressPayloadTests(unittest.TestCase):
    def test_scripted_is_the_request_it_always_was(self):
        self.assertEqual(press_payload({"buttons": ["a", "B"]}),
                         {"op": "press", "buttons": 0x3000, "frames": 8})
        self.assertEqual(press_payload({"buttons": ["start"], "pad": 1, "frames": 2, "lx": 0.5}),
                         {"op": "press", "buttons": 0x10, "frames": 2, "pad": 1, "lx": 0.5})
        self.assertEqual(press_payload({"buttons": ["a"], "mode": "scripted"}),
                         {"op": "press", "buttons": 0x1000, "frames": 8})

    def test_joypad_mode_marks_the_request(self):
        self.assertEqual(press_payload({"buttons": ["a"], "mode": "joypad"}),
                         {"op": "press", "buttons": 0x1000, "frames": 8, "joypad": True})

    def test_joypad_player_names_the_pad(self):
        payload = press_payload({"buttons": ["start"], "mode": "Joypad", "player": 2})
        self.assertEqual((payload["player"], payload["joypad"]), (2, True))
        self.assertNotIn("pad", payload)

    def test_joypad_stick_alone(self):
        payload = press_payload({"buttons": [], "mode": "joypad", "ly": 1, "rt": 0.25})
        self.assertEqual((payload["buttons"], payload["ly"], payload["rt"]), (0, 1.0, 0.25))

    def test_the_dummy_pad_is_a_joypad_press(self):
        want = {"op": "press", "buttons": 0x10, "frames": 8, "joypad": True, "dummy": True}
        self.assertEqual(press_payload({"buttons": ["start"], "pad": "dummy"}), want)
        self.assertEqual(press_payload({"buttons": ["start"], "pad": " Dummy ", "mode": "joypad"}), want)
        stick = press_payload({"buttons": [], "pad": "dummy", "lx": -1, "frames": 90})
        self.assertEqual(stick, {"op": "press", "buttons": 0, "frames": 90, "lx": -1.0,
                                 "joypad": True, "dummy": True})

    def test_refusals(self):
        for bad in ({"buttons": []}, {"buttons": ["z"]}, {"buttons": ["a"], "mode": "keyboard"},
                    {"buttons": ["a"], "player": 1},
                    {"buttons": ["a"], "mode": "joypad", "player": 3},
                    {"buttons": ["a"], "mode": "joypad", "player": 1, "pad": 0},
                    {"buttons": ["a"], "pad": "dummy", "mode": "scripted"},
                    {"buttons": ["a"], "pad": "dummy", "player": 2},
                    {"buttons": [], "pad": "dummy"}):
            self.assertFalse(press_payload(bad).get("ok", True), bad)


if __name__ == "__main__":
    unittest.main()
