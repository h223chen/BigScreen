# Task list of improvements/requests/things that need to be done

## v 0.1.0

- [ ] goal 1: make BigScreen interaction related to an object
  - Why: current F8 menu is terrible UX (the mouse interaction moves the player's PoV, bad contrast), takes you out of the game
  - Goal: ideally spawn a screen item (similar to other in-game items) that a user can interact with (when clicking)
  - Stretch goal: have BigScreen be 2 parts: BigScreen (actually placing a screen) and BigRemote (selects the youtube channel, play/pause).
    - Design references:
      - The spawned BigScreen is fine, ideally we have a smaller item that someone can hold and place in the world (what if it's a BigProjector (short throw projector model))
      - Might need "interaction points" to place the short throw projector in the world, like the Big Walk round lights and their seats.
      - The whiteboard interaction -> click to hold, click to change text, type/paste in text, then click to get out (and a hook to place it on)
- [ ] goal 2 (can be punted to 0.1.1): BigProjector/BigRemote saved in the world when session ends.

### Goal 1
**What we found in the game's code (2026-09-19)**

Short version: **the game already has all three systems we need**, and one of them we can
use almost immediately.

**Typing text — solved, low risk.** The whiteboard's text entry is a class called
`SignTextInput`, and it offers three simple calls: open the box (optionally pre-filled),
ask whether the player is still typing, and take the finished text. That is the game's own
keyboard UI, so it looks right, reads clearly, and does not fight for the mouse. It is a
direct replacement for the URL box in the F8 panel and involves no networking at all.

**Clicking on things — "Peck".** Pecking is Big Walk's word for interacting. Any object
you can interact with carries a `PeckSwitch`, which handles the crosshair, whether the
interaction is currently blocked, and whether the player is holding the thing. What happens
when you peck is a separate component, and the game ships about forty of them (teleport,
play music, change material, and so on). There is a template class showing the shape a new
one takes.

The important one is `PeckEffectTextInput`: it *is* the whiteboard. Peck it, the text box
opens, you type, and the result is sent to everyone. It is a complete worked example of
exactly the interaction you described.

**Placing things — "props" and "homes".** A `Prop` is a carryable object; a `PropHome` is a
spot it can sit in. That is precisely the "interaction points, like the round lights and
their seats" idea. Props can also be carried in a player's pocket, and "brandishing" is the
game's term for holding one up. So the BigRemote concept already maps onto vocabulary the
game understands.

**Bonus.** The game itself uses Unity's video player and pipes its sound through its own
audio system (`VideoPlayerAudioAssigner`). That is why the video component survived in the
build at all, and it may offer a better way to position our audio than what we do now.

**The catch**

The peck and prop systems are **networked** - they sync through Mirror, the game's own
networking. Our architecture forbids creating networked objects, because a vanilla player's
game has no idea what our object is and would break. So we cannot make a real in-game prop
that everyone sees. We can only make something local that looks like one, and sync it
ourselves over our own channel.

**Suggested order**

1. **Swap the URL box for `SignTextInput`.** Safe, self-contained, fixes the worst part of
    the current UX. Keeps F8 for everything else so nothing that already works can break.
2. **Make the placed screen peckable.** Medium risk - we do not yet know whether a
    `PeckSwitch` works on an object that was never registered with the networked manager.
    Needs testing in-game, not more reading.
3. **BigRemote as a carried prop with a home.** Highest risk, because the prop system looks
    the most deeply networked. Worth doing last, once 1 and 2 have taught us how much of the
    system works off the network path.

Still unknown, and only answerable by experiment: what information a peck actually carries,
whether the crosshair notices a `PeckSwitch` we made ourselves, and whether prop placement
has any local-only path.
