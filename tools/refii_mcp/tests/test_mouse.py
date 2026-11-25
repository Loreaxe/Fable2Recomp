"""`refii_mouse` argument checks and the request they make; no game needed."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from refii_mcp.tools.drive import mouse_payload


class MousePayloadTests(unittest.TestCase):
    def test_move_in_pixels(self):
        self.assertEqual(mouse_payload({"x": 640, "y": 360}),
                         {"op": "mouse", "x": 640.0, "y": 360.0, "normalized": False})

    def test_move_normalized(self):
        self.assertEqual(mouse_payload({"x": 0.5, "y": 0.25, "normalized": True})["normalized"], True)
        self.assertFalse(mouse_payload({"x": 1.5, "y": 0.2, "normalized": True}).get("ok", True))

    def test_click_without_a_point_stays_where_it_is(self):
        payload = mouse_payload({"click": "Right"})
        self.assertEqual(payload, {"op": "mouse", "click": "right", "frames": 3})

    def test_click_at_a_point(self):
        payload = mouse_payload({"x": 10, "y": 20, "click": "middle", "frames": 0})
        self.assertEqual((payload["click"], payload["frames"], payload["x"]), ("middle", 1, 10.0))

    def test_wheel_is_signed_negative_up(self):
        self.assertEqual(mouse_payload({"wheel": "up", "steps": 3})["wheel"], -3)
        self.assertEqual(mouse_payload({"wheel": "down"})["wheel"], 1)

    def test_refusals(self):
        for bad in ({}, {"x": 1}, {"click": "side"}, {"wheel": "left"}, {"wheel": "up", "steps": 0},
                    {"x": -1, "y": 2}):
            self.assertFalse(mouse_payload(bad).get("ok", True), bad)


if __name__ == "__main__":
    unittest.main()
