using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.InputSystem;

namespace Nightshare.UI
{
    /// <summary>
    /// What the Nightshare menu contains and how the keyboard moves through it.
    /// <para>
    /// Kept apart from <see cref="LobbyMenu"/>, which only knows how to draw rows. This
    /// decides what the rows say, which of them can be used, and what Enter does.
    /// </para>
    /// <para>
    /// <b>The menu is the join flow now.</b> Hosting and joining used to be hotkeys acting
    /// on a config file, which meant a guest connected from the main menu before it had a
    /// world, and a long list of bugs followed from that one fact. Opening a menu requires
    /// being in the game, so the thing that used to go wrong is no longer reachable.
    /// </para>
    /// </summary>
    internal sealed class LobbyController
    {
        private readonly LobbyMenu _menu = new LobbyMenu();
        private readonly List<MenuItem> _items = new List<MenuItem>();

        private int _selected;
        private bool _open;

        /// <summary>Which field is being typed into, or None.</summary>
        private Editing _editing = Editing.None;
        private readonly StringBuilder _buffer = new StringBuilder();

        private enum Editing { None, Address, Port }

        public bool IsOpen => _open;

        /// <summary>
        /// True while the menu wants the keyboard to itself, so the rest of the mod's
        /// hotkeys stand down. Without this, typing a port number into the address field
        /// would also be firing the join and leave keys.
        /// </summary>
        public bool CapturesInput => _open;

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        public void Open()
        {
            if (!_menu.Available) _menu.Build();
            if (!_menu.Available) return;

            _open = true;
            _selected = 0;
            _editing = Editing.None;
            _menu.SetVisible(true);
        }

        public void Close()
        {
            _open = false;
            _editing = Editing.None;
            _menu.SetVisible(false);
        }

        public void Destroy() => _menu.Destroy();

        // ---------------------------------------------------------------- frame

        public void Tick()
        {
            if (!_open) return;

            try
            {
                BuildItems();

                var keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    if (_editing != Editing.None) HandleTyping(keyboard);
                    else HandleNavigation(keyboard);
                }

                _menu.Render(Heading(), Status(), _items, _selected, Footer());
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"Lobby menu threw, closing it: {ex.Message}");
                Close();
            }
        }

        // ---------------------------------------------------------------- contents

        private static NightshareCore Core => NightshareCore.Instance;

        private void BuildItems()
        {
            _items.Clear();

            var session = Core.SessionSummary;
            var inWorld = Core.HasWorld;

            // NOTHING HERE CLOSES THE MENU EXCEPT CLOSE.
            //
            // Hosting used to close it, which left a player with no confirmation that
            // anything had happened. The menu stays up and redraws into its new state, so
            // pressing Host visibly becomes "Hosting, 0 other players". Shutting it is the
            // player's decision and always the same key.

            if (session.Active)
            {
                _items.Add(new MenuItem
                {
                    Label = session.IsHost ? "Stop Hosting" : "Leave Session",
                    Activate = () => Core.Leave(),
                });

                _items.Add(new MenuItem { Label = "Close", Activate = Close });
                ClampSelection();
                return;
            }

            if (_browsing)
            {
                BuildBrowserItems(inWorld);
                _items.Add(new MenuItem { Label = "Back", Activate = () => StopBrowsing() });
                ClampSelection();
                return;
            }

            _items.Add(new MenuItem
            {
                Label = "Host A Session",
                Enabled = inWorld,
                DisabledReason = "Load your save first. A session needs a world.",
                Activate = () => Core.StartHosting(),
            });

            _items.Add(new MenuItem
            {
                Label = "Find A Session",
                Enabled = inWorld,
                DisabledReason = "Load your save first. You join from inside your own game.",
                Activate = () => StartBrowsing(),
            });

            _items.Add(new MenuItem
            {
                Label = "Join By Address",
                Value = session.Endpoint,
                Enabled = inWorld,
                DisabledReason = "Load your save first. You join from inside your own game.",
                Activate = () => Core.StartJoining(),
            });

            _items.Add(new MenuItem
            {
                Label = "Address",
                Value = _editing == Editing.Address ? _buffer + "_" : Core.LobbyAddress,
                Activate = () => BeginEditing(Editing.Address, Core.LobbyAddress),
            });

            _items.Add(new MenuItem
            {
                Label = "Port",
                Value = _editing == Editing.Port ? _buffer + "_" : Core.LobbyPort.ToString(),
                Activate = () => BeginEditing(Editing.Port, Core.LobbyPort.ToString()),
            });

            _items.Add(new MenuItem { Label = "Close", Activate = Close });
            ClampSelection();
        }

        private void ClampSelection()
        {
            if (_selected >= _items.Count) _selected = _items.Count - 1;
            if (_selected < 0) _selected = 0;
        }

        // ---------------------------------------------------------------- browsing

        private bool _browsing;
        private readonly List<Core.Discovery.FoundLobby> _lobbies =
            new List<Core.Discovery.FoundLobby>();

        private void StartBrowsing()
        {
            _browsing = true;
            _selected = 0;
            Core.StartLookingForSessions();
        }

        private void StopBrowsing()
        {
            _browsing = false;
            _selected = 0;
            Core.StopLookingForSessions();
        }

        private void BuildBrowserItems(bool inWorld)
        {
            Core.CollectFoundSessions(_lobbies);

            if (_lobbies.Count == 0)
            {
                _items.Add(new MenuItem
                {
                    Label = "Looking...",
                    Enabled = false,
                    DisabledReason = "Sessions on your network appear here within a second or two.",
                });
                return;
            }

            foreach (var lobby in _lobbies)
            {
                // Captured for the closure. Without this every row would act on whatever
                // the loop variable ended up as.
                var found = lobby;

                _items.Add(new MenuItem
                {
                    Label = found.HostName,
                    Value = found.PlayerCount == 0 ? "waiting" : $"{found.PlayerCount} playing",
                    Enabled = inWorld && found.Compatible,

                    // An incompatible session is listed, not hidden. "Different game
                    // version" is an answer; a session that never appears is not.
                    DisabledReason = found.Compatible
                        ? "Load your save first."
                        : found.Incompatibility,

                    Activate = () =>
                    {
                        Core.SetLobbyAddress(found.Address);
                        Core.SetLobbyPort(found.Port);
                        Core.StartJoining();
                        StopBrowsing();
                    },
                });
            }
        }

        private string Heading() => "Nightshare";

        /// <summary>
        /// The line under the heading.
        /// <para>
        /// <b>A failure belongs here, not only in the log.</b> Pressing Host once failed on
        /// an unbindable address and the menu carried on saying "Not in a session", so it
        /// read as the button doing nothing. The log said exactly what had happened. Nobody
        /// should have to read a log to find out that a button they pressed failed.
        /// </para>
        /// </summary>
        private string Status()
        {
            var s = Core.SessionSummary;

            if (!string.IsNullOrEmpty(s.LastError)) return s.LastError;

            if (!s.Active)
            {
                if (!Core.HasWorld) return "Not in a session. Load your save to begin.";
                return s.Connecting ? "Connecting..." : "Not in a session";
            }

            var who = s.IsHost ? "Hosting" : $"Visiting {s.HostName}";
            return s.PeerCount == 1
                ? $"{who}, 1 other player"
                : $"{who}, {s.PeerCount} other players";
        }

        private string Footer()
        {
            if (_editing != Editing.None)
                return "Type to edit    Enter Save    Esc Cancel";

            if (_selected >= 0 && _selected < _items.Count)
            {
                var item = _items[_selected];
                if (!item.Enabled && !string.IsNullOrEmpty(item.DisabledReason))
                    return item.DisabledReason;
            }

            return "Up Down Move    Enter Select    Esc Close";
        }

        // ---------------------------------------------------------------- input

        private void HandleNavigation(Keyboard keyboard)
        {
            if (Pressed(keyboard, Key.Escape)) { Close(); return; }

            if (Pressed(keyboard, Key.UpArrow)) Move(-1);
            else if (Pressed(keyboard, Key.DownArrow)) Move(1);
            else if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter))
            {
                if (_selected < 0 || _selected >= _items.Count) return;

                var item = _items[_selected];
                if (!item.Enabled || item.Activate == null) return;

                item.Activate();
            }
        }

        /// <summary>
        /// Move the selection, skipping nothing.
        /// <para>
        /// Disabled rows are still selectable on purpose: selecting one is how its reason
        /// reaches the footer. Skipping them would leave a player staring at a greyed option
        /// with no way to find out why.
        /// </para>
        /// </summary>
        private void Move(int delta)
        {
            if (_items.Count == 0) return;

            _selected += delta;
            if (_selected < 0) _selected = _items.Count - 1;
            if (_selected >= _items.Count) _selected = 0;
        }

        private void BeginEditing(Editing what, string current)
        {
            _editing = what;
            _buffer.Clear();
            _buffer.Append(current ?? "");
        }

        /// <summary>
        /// Text entry, built from individual keys rather than the Input System's text event.
        /// <para>
        /// An address and a port need digits, dots and backspace and nothing else, which is
        /// a small enough set to map directly. That also sidesteps needing a colon, which is
        /// a shifted key: the endpoint is split into two fields precisely so nobody has to
        /// type one.
        /// </para>
        /// </summary>
        private void HandleTyping(Keyboard keyboard)
        {
            if (Pressed(keyboard, Key.Escape)) { _editing = Editing.None; return; }

            if (Pressed(keyboard, Key.Enter) || Pressed(keyboard, Key.NumpadEnter))
            {
                Commit();
                return;
            }

            if (Pressed(keyboard, Key.Backspace) && _buffer.Length > 0)
            {
                _buffer.Length--;
                return;
            }

            for (var d = 0; d <= 9; d++)
            {
                if (Pressed(keyboard, TopRowDigits[d]) || Pressed(keyboard, NumpadDigits[d]))
                {
                    if (_buffer.Length < 21) _buffer.Append((char)('0' + d));
                    return;
                }
            }

            // Dots only belong in an address.
            if (_editing == Editing.Address &&
                (Pressed(keyboard, Key.Period) || Pressed(keyboard, Key.NumpadPeriod)))
            {
                if (_buffer.Length < 21) _buffer.Append('.');
            }
        }

        private void Commit()
        {
            // Read which field this was BEFORE clearing it. Testing _editing after the
            // reset is always false, which silently routed every port edit into the
            // address field.
            var what = _editing;
            var text = _buffer.ToString().Trim();

            _editing = Editing.None;

            if (text.Length == 0) return;   // an empty field keeps the old value

            if (what == Editing.Port)
            {
                if (int.TryParse(text, out var port) && port > 0 && port <= 65535)
                    Core.SetLobbyPort(port);
                return;
            }

            Core.SetLobbyAddress(text);
        }

        /// <summary>
        /// Digit keys, listed rather than calculated.
        /// <para>
        /// <b>This was <c>Key.Digit0 + d</c> and it was wrong.</b> The Input System's enum
        /// runs Digit1 through Digit9 and then Digit0, so adding to Digit0 walks off into
        /// the modifier keys and only "0" ever matched on the top row. Typing an address
        /// produced a fragment of one, which was then saved and used, and hosting failed on
        /// a bind to 0.0.0.7.
        /// </para>
        /// <para>
        /// Arithmetic on enum members only works when the values are contiguous AND in the
        /// order you assume. Both halves need checking, and listing them needs neither.
        /// </para>
        /// </summary>
        private static readonly Key[] TopRowDigits =
        {
            Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4,
            Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9,
        };

        private static readonly Key[] NumpadDigits =
        {
            Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4,
            Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9,
        };

        private static bool Pressed(Keyboard keyboard, Key key)
        {
            if (key == Key.None) return false;

            var control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
        }
    }
}
