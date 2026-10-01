# Changelog

## 0.1.4

### Fixed

- Moving the selection in the panel no longer flickers a line of text over the row above or
  below it.
- The selected row's marker now changes colour straight away when you switch theme, instead
  of keeping the old colour until you moved to a different row.

### Notes

- The selected row is now marked by a stripe down its left edge rather than a tint across
  the whole row. That is the fix, not a style change: a marker that overlaps the row's text
  is what caused the flicker.

## 0.1.3

### Fixed

- At very low frame rates some people could stay invisible even after you walked right up to
  them. Only affected you with Hide Faraway Crowds turned on.

### Added

- Protect Named NPCs, off by default. With Hide Faraway Crowds on, it keeps story characters
  and anyone who is talking visible while the rest of the crowd still fades out at a
  distance.

### Notes

- Protect Named NPCs covers a handful of people out of a few hundred, so it costs almost
  nothing in frames.
- The low frame rate fix and the tests that go with it came from
  [@master63dotcom](https://github.com/master63dotcom).

## 0.1.2

### Fixed

- NPCs disappearing on Medium and High. Lumen was switching off the level of detail the game
  had chosen to draw, which left nothing on screen until you walked close enough for it to
  pick a different one. Very High never showed it, because that preset happens to pick the
  one Lumen kept.
- Turning Crowd Performance off now puts everything back immediately. It used to stop
  working but leave its changes in place until you restarted the game. Same for the
  individual settings.

### Notes

- Remove Duplicate Characters has been removed entirely. It was based on a misunderstanding
  of how the game draws characters and could never have helped. If you had it on, nothing of
  value is lost.
- What remains is Skip Unused Shadow Work, worth a few free frames with no visible change,
  and the optional Hide Faraway Crowds.
- The off switch problem was found by
  [@master63dotcom](https://github.com/master63dotcom).

## 0.1.1

### Fixed

- NPCs disappearing unless you were close to them. Lumen always kept the most detailed
  version of a character, even when the game had a different one switched on, which at a
  distance meant hiding the version you could actually see. It now keeps whichever one the
  game is already using.

## 0.1.0

### Added

- First release. Skip Unused Shadow Work, Hide Faraway Crowds, Borderless Window and six
  colour themes, on F10.
