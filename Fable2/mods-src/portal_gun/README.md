# Portal Gun

A gun that fires a pair of linked portals onto walls, floors and ceilings. Look through one to see
out of the other, and step in to come out there.

**G** (the mod wheel on a pad) turns the gun you have equipped into the Portal Gun, and **G** again turns it
back. You keep the game's stance and aim for that gun. Portal mode belongs to whoever turned it on:
in co-op the other player's **G** does nothing until it is off again.

**Hold the left mouse button to aim and release to fire the blue portal; do the same with the right
button for the orange one.** While portal mode is on, the right
button and `T` aren't the game's lock-on, and with a mouse melee and magic wait until you turn it off.

On a controller, **hold Y to aim, and press LT for the blue portal or RT for the orange one**. Letting
go of Y without a trigger opens nothing.

While you aim, an outline shows where the portal would open, red when it wouldn't fit. Portals close
when you change level.

Options › Mods › Portal Gun sets how sharp the view through a portal is and how far away it draws;
lower runs faster.

On a phone the **Portal Gun** pill in the top row of the touch controls is G. While portal mode is
on, the screen's Y fires the colour it is labelled, **Blue** or **Orange**, and the **Swap Colour**
pill under Portal Gun changes it; Lock, Melee and Magic stay the game's. Hold Y and the outline shows
where the portal would open, slide your thumb off the button to aim, and let go to fire. The view
stays over the shoulder however long you hold; the gun goes off when you let go.

## Limits of this version

- Only the hero goes through.
- Walls stay solid, so the hero moves on touching a portal rather than walking through gradually.
- Speed isn't carried through, and neither is the way you're facing.

## How it works

`mod/camera.lua` keeps the camera where the mouse puts it while portal mode is on. `mod/init.lua`
cancels the gun's shot on `ranged_shot`, finds where it lands with `Refii.line_of_sight`, slides the
portal to fit and moves the hero with `Refii.set_position`. `godot/portal_gun.gd` aims along the
portal player's own view (`camera_ray`, their half on a split screen) and draws each portal: a quad
showing what a second camera, placed from that player's drawn camera (`camera_pose`), sees through its
partner.

The model is `art/inventory/weapons/portal_gun/portal_gun.mdl`, built by `source/make_model.py` and
compiled against the game's Master Flintlock Rifle, whose grip points it keeps. `godot/portal_gun.gd`
draws the gun with it while portal mode is on, so the item itself never changes. The art is the mod's
own; the sounds are the game's spell cues.
