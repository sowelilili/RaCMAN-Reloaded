using System.Net;

namespace RaCMAN.Protocol.Testing;

/// <summary>
/// Per-user settings, revision 1.24 (qwark build 56): the console-wide switch, the user signed in
/// at the fake XMB, a profile per user and the shared settings beside them, PROFILE_INFO,
/// PROFILE_ENABLE and COMBO_OVERRIDE.
/// <para>
/// The fake keeps one live copy of every per-user value it models, where the rest of the server
/// reads and writes them as it always has: <c>menu.help</c>, the selected slot, the "on boot"
/// toggles, the mods' auto flags, the position slots, the planet selection and the manip
/// choices. Whenever the active profile changes, the live values are stored back to whoever they
/// belonged to (the shared settings or a user's profile) and the new owner's are put in.
/// </para>
/// <para>
/// The combos are not swapped: <see cref="Combos"/> is always the shared set, which COMBO_SET
/// writes, and each profile keeps a map of per-combo overrides that COMBO_LIST lays over it
/// while that profile is active. The combo switch, <c>combo.enabled</c>, is the console's.
/// </para>
/// </summary>
public sealed partial class FakeQwarkServer
{
    /// <summary>What one owner (the shared settings, or a console user) keeps per user.</summary>
    private sealed class PerUserSettings
    {
        public bool MenuHelp = true;
        public byte SelectedSlot;
        public ulong ToggleAuto;
        public HashSet<string> ModAuto = new(StringComparer.Ordinal);
        public Dictionary<byte, byte[]?[]> Positions = new();
        public (byte Planet, PlanetFlags Flags) LegacyPlanet;
        public Dictionary<GameId, (byte Planet, PlanetFlags Flags)> Planets = new();
        public Dictionary<GameId, (byte Selected, bool AutoReset, bool Remember)> Manips = new();

        public PerUserSettings Clone() => new()
        {
            MenuHelp = MenuHelp,
            SelectedSlot = SelectedSlot,
            ToggleAuto = ToggleAuto,
            ModAuto = new HashSet<string>(ModAuto, StringComparer.Ordinal),
            Positions = ClonePositions(Positions),
            LegacyPlanet = LegacyPlanet,
            Planets = new(Planets),
            Manips = new(Manips),
        };
    }

    private sealed class UserProfile
    {
        public PerUserSettings Settings = new();

        /// <summary>The profile's per-combo overrides: the masks that win over the shared ones while it is active.</summary>
        public readonly Dictionary<ComboAction, uint> ComboOverrides = new();
    }

    private bool _profilesSupported;
    private bool _profilesEnabled;
    private uint _signedInUser = 1;
    private uint _xmbUser = 1;
    private byte _profileSeq;
    private PerUserSettings _sharedSettings = new();
    private readonly Dictionary<uint, UserProfile> _profiles = new();

    /// <summary>Whose per-user values the live state holds: 0 the shared settings, n user n, null nobody's.</summary>
    private uint? _liveSettingsOwner = 0;

    /// <summary>
    /// SessionInfo byte 29 bit1: the module has per-user settings and the console has users. Off by
    /// default, as every module before build 56 has it, so a test that does not ask sees none of
    /// it. Never reported while <see cref="Emulator"/> is set, as qwark-rpcs3 never sets it: there
    /// PROFILE_INFO answers zeros and the other two UNSUPPORTED.
    /// </summary>
    public bool ProfilesSupported
    {
        get { lock (_gate) return _profilesSupported; }
        set
        {
            lock (_gate)
            {
                _profilesSupported = value;
                SyncProfiles();
            }
        }
    }

    /// <summary>
    /// config.txt's <c>profiles.enabled</c>, as PROFILE_ENABLE writes it. Setting it here is a
    /// config edit behind the client's back: it moves <see cref="ProfileSeq"/> as a reload would.
    /// </summary>
    public bool ProfilesEnabled
    {
        get { lock (_gate) return _profilesEnabled; }
        set
        {
            lock (_gate)
            {
                _profilesEnabled = value;
                SyncProfiles();
                _profileSeq++;
            }
        }
    }

    /// <summary>
    /// The XMB user number qwark takes as signed in (1 at start, 0 for nobody). Setting it here
    /// takes effect at once, wherever the session is: how a test sets a console up. For a user
    /// change the way the console sees one, at the XMB only, use <see cref="SignInAtXmb"/>.
    /// </summary>
    public uint SignedInUser
    {
        get { lock (_gate) return _signedInUser; }
        set
        {
            lock (_gate)
            {
                _xmbUser = value;
                TakeXmbUser();
            }
        }
    }

    /// <summary>
    /// The name PROFILE_INFO reports, per user number. Empty, as on the real module, which leaves
    /// the field empty for a console user and reserves it for named profiles (a later revision).
    /// A test hook for that path only.
    /// </summary>
    public Dictionary<uint, string> ProfileNames { get; } = new();

    /// <summary>
    /// A user signs in at the XMB (qwark-host's <c>user &lt;n&gt;</c>). qwark only looks while the session is at the XMB, so in a game the change
    /// waits until the session gets there.
    /// </summary>
    public void SignInAtXmb(uint user)
    {
        lock (_gate)
        {
            _xmbUser = user;
            if (_session.State == SessionState.Xmb) TakeXmbUser();
        }
    }

    /// <summary>The profile_seq the session reports while profiles are supported.</summary>
    public byte ProfileSeq
    {
        get { lock (_gate) return _profileSeq; }
    }

    /// <summary>Whether user <paramref name="user"/>'s profile folder exists (PROFILE_INFO bit5 for that user).</summary>
    public bool HasProfile(uint user)
    {
        lock (_gate) return _profiles.ContainsKey(user);
    }

    /// <summary>PROFILE_INFO as the fake would answer it now.</summary>
    public ProfileInfo CurrentProfileInfo
    {
        get { lock (_gate) return BuildProfileInfo(); }
    }

    /// <summary>
    /// <c>menu.help</c> as owner <paramref name="user"/> keeps it: 0 for the shared settings, a
    /// user number for that user's profile (the defaults while it has none).
    /// </summary>
    public bool MenuHelpFor(uint user)
    {
        lock (_gate) return SettingsOf(user).MenuHelp;
    }

    /// <summary>Whether the mod in <paramref name="dirName"/> loads automatically for owner <paramref name="user"/>.</summary>
    public bool ModAutoFor(uint user, string dirName)
    {
        lock (_gate) return SettingsOf(user).ModAuto.Contains(dirName);
    }

    /// <summary>User <paramref name="user"/>'s combo overrides, kept whether or not the profile is active; empty without a profile.</summary>
    public IReadOnlyDictionary<ComboAction, uint> ComboOverridesFor(uint user)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(user, out var p)
                ? new Dictionary<ComboAction, uint>(p.ComboOverrides)
                : new Dictionary<ComboAction, uint>();
        }
    }

    // ------------------------------------------------------------------ under the gate

    /// <summary>Byte 29 bit1, as this fake reports it.</summary>
    private bool ProfilesLive => _profilesSupported && !_releaseModule && (_session.Flags & SessionFlags.Emulator) == 0;

    /// <summary>Whose profile is active: the signed-in user's with the switch on, 0 (the shared settings) otherwise.</summary>
    private uint ActiveUser => ProfilesLive && _profilesEnabled ? _signedInUser : 0;

    /// <summary>The active profile's combo overrides, or null when no profile with any is active.</summary>
    private Dictionary<ComboAction, uint>? ActiveComboOverrides =>
        ActiveUser is var user && user != 0 && _profiles.TryGetValue(user, out var p) ? p.ComboOverrides : null;

    /// <summary>
    /// COMBO_LIST's rows as the module lays them out: exactly one per action, in action order
    /// (COMBO_COUNT, seven since revision 1.23), each with the mask in force: the active profile's
    /// override where it has one, flagged, and the shared mask otherwise, 0 included. Caller holds
    /// the gate.
    /// </summary>
    private List<(ComboAction Action, ComboEntryFlags Flags, uint Mask)> CombosInForce()
    {
        var overrides = ActiveComboOverrides;
        var rows = new List<(ComboAction, ComboEntryFlags, uint)>();
        for (var action = ComboAction.SavePosition; action <= ComboAction.ToggleFastLoads; action++)
        {
            rows.Add(overrides is not null && overrides.TryGetValue(action, out var own)
                ? (action, ComboEntryFlags.Overridden, own)
                : (action, ComboEntryFlags.None, Combos.GetValueOrDefault(action)));
        }

        return rows;
    }

    private ProfileFlags ReportedProfileFlags(bool withHasProfile = false)
    {
        if (!ProfilesLive) return ProfileFlags.None;

        var flags = ProfileFlags.Supported;
        if (_profilesEnabled) flags |= ProfileFlags.On;
        if (ActiveUser != 0) flags |= ProfileFlags.Active;
        if (withHasProfile && _signedInUser != 0 && _profiles.ContainsKey(_signedInUser)) flags |= ProfileFlags.HasProfile;
        return flags;
    }

    private ProfileInfo BuildProfileInfo()
    {
        if (!ProfilesLive) return ProfileInfo.None;

        uint user = _signedInUser;
        return new ProfileInfo(
            ReportedProfileFlags(withHasProfile: true),
            ActiveUser != 0 ? ProfileKind.User : ProfileKind.Shared,
            _profileSeq,
            user,
            user == 0 ? string.Empty : $"user-{user:D8}",
            user != 0 && ProfileNames.TryGetValue(user, out var name) ? name : string.Empty);
    }

    /// <summary>The XMB's user becomes qwark's, if it is another one.</summary>
    private void TakeXmbUser()
    {
        if (_xmbUser == _signedInUser) return;

        _signedInUser = _xmbUser;
        SyncProfiles();
        _profileSeq++;
    }

    /// <summary>The per-user values owner <paramref name="user"/> keeps, read from the live state when they are its.</summary>
    private PerUserSettings SettingsOf(uint user)
    {
        if (_liveSettingsOwner == user) return CaptureLive();
        if (user == 0) return _sharedSettings;
        return _profiles.TryGetValue(user, out var p) ? p.Settings : new PerUserSettings();
    }

    /// <summary>The live per-user values go back to whoever they belong to.</summary>
    private void StoreLive()
    {
        if (_liveSettingsOwner is not { } owner) return;

        var live = CaptureLive();
        if (owner == 0) _sharedSettings = live;
        else if (_profiles.TryGetValue(owner, out var p)) p.Settings = live;

        // A profile never set up keeps nothing: a write to it would have set it up.
    }

    /// <summary>Puts the active owner's values into the live state, if they are not there already.</summary>
    private void SyncProfiles()
    {
        uint owner = ActiveUser;
        if (owner == _liveSettingsOwner) return;

        StoreLive();
        ApplyLive(owner == 0 ? _sharedSettings
            : _profiles.TryGetValue(owner, out var p) ? p.Settings
            : new PerUserSettings());
        _liveSettingsOwner = owner;
    }

    /// <summary>
    /// Sets up <paramref name="user"/>'s profile if it never has been: a copy of the shared
    /// per-user settings, or the defaults. A profile that exists is never overwritten.
    /// </summary>
    private bool SetUpProfile(uint user, ProfileSetup setup)
    {
        if (user == 0 || setup == ProfileSetup.None || _profiles.ContainsKey(user)) return false;

        StoreLive();
        _profiles[user] = new UserProfile
        {
            Settings = setup == ProfileSetup.Copy ? _sharedSettings.Clone() : new PerUserSettings(),
        };

        // The live state held the new profile's defaults, which belong to nobody: the next sync
        // puts the profile just made in their place.
        if (_liveSettingsOwner == user) _liveSettingsOwner = null;
        return true;
    }

    /// <summary>
    /// The first write to an active profile that has never been set up creates it: the live state
    /// is its defaults (plus that write), so it is the profile's from now on. No profile_seq move:
    /// a single key write is not one.
    /// </summary>
    private UserProfile? EnsureActiveProfile()
    {
        uint user = ActiveUser;
        if (user == 0) return null;
        if (_profiles.TryGetValue(user, out var profile)) return profile;

        profile = new UserProfile { Settings = CaptureLive() };
        _profiles[user] = profile;
        return profile;
    }

    /// <summary>The ops that write a per-user key: any of them sets a new profile up from the defaults first.</summary>
    private static bool WritesPerUserKey(Opcode opcode) => opcode is
        Opcode.MenuHelp or Opcode.FeatureSetAuto or Opcode.ModSetAuto or Opcode.PlanetSelect
        or Opcode.PosSelect or Opcode.PosSave or Opcode.PosClear or Opcode.PosEdit or Opcode.PosStore
        or Opcode.ManipSelect or Opcode.ManipOptions;

    /// <summary>Every request, with the profile in force put in first and a write to a new profile setting it up.</summary>
    private (Status, byte[]?) Handle(Opcode opcode, byte[] payload, IPEndPoint remote)
    {
        lock (_gate)
        {
            SyncProfiles();
            var answer = HandleOp(opcode, payload, remote);
            if (answer.Item1 == Status.Ok && WritesPerUserKey(opcode)) EnsureActiveProfile();
            return answer;
        }
    }

    private (Status, byte[]?) HandleProfiles(Opcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case Opcode.ProfileInfo:
                return (Status.Ok, BuildProfileInfo().ToBytes());

            case Opcode.ProfileEnable:
            {
                if (payload.Length < 2 || payload[0] > 1 || payload[1] > 2) return (Status.BadArg, null);
                if (!ProfilesLive) return (Status.Unsupported, null);

                // The setup acts only as the switch goes on.
                bool on = payload[0] != 0;
                if (on) SetUpProfile(_signedInUser, (ProfileSetup)payload[1]);
                _profilesEnabled = on;
                SyncProfiles();
                _profileSeq++;
                return (Status.Ok, null);
            }

            case Opcode.ComboOverride:
            {
                if (payload.Length < 8) return (Status.BadArg, null);
                var r = new SpanReader(payload);
                var action = (ComboAction)r.ReadU8();
                byte set = r.ReadU8();
                r.Skip(2);
                uint mask = r.ReadU32();

                if (action > ComboAction.ToggleFastLoads || set > 1) return (Status.BadArg, null);
                if (!ProfilesLive) return (Status.Unsupported, null);
                if (EnsureActiveProfile() is not { } profile) return (Status.NotFound, null);

                if (set == 1) profile.ComboOverrides[action] = mask;
                else profile.ComboOverrides.Remove(action);
                return (Status.Ok, null);
            }

            default:
                return (Status.UnknownOp, null);
        }
    }

    private PerUserSettings CaptureLive() => new()
    {
        MenuHelp = (_session.Flags & SessionFlags.MenuHelpOff) == 0,
        SelectedSlot = _session.SelectedSlot,
        ToggleAuto = _session.ToggleAuto,
        ModAuto = Mods.Where(m => m.Auto).Select(m => m.DirName).ToHashSet(StringComparer.Ordinal),
        Positions = ClonePositions(_positions),
        LegacyPlanet = _legacyPlanetSelection,
        Planets = new(_planetSelections),
        Manips = _manipConfigs.ToDictionary(kv => kv.Key, kv => (kv.Value.Selected, kv.Value.AutoReset, kv.Value.Remember)),
    };

    private void ApplyLive(PerUserSettings settings)
    {
        _session = _session with
        {
            Flags = settings.MenuHelp ? _session.Flags & ~SessionFlags.MenuHelpOff : _session.Flags | SessionFlags.MenuHelpOff,
            SelectedSlot = settings.SelectedSlot,
            ToggleAuto = settings.ToggleAuto,
        };

        for (int i = 0; i < Mods.Count; i++)
        {
            var mod = Mods[i];
            bool auto = settings.ModAuto.Contains(mod.DirName);
            Mods[i] = mod with { Flags = auto ? mod.Flags | ModFlags.Auto : mod.Flags & ~ModFlags.Auto };
        }

        _positions.Clear();
        foreach (var (planet, slots) in ClonePositions(settings.Positions)) _positions[planet] = slots;

        _legacyPlanetSelection = settings.LegacyPlanet;
        _planetSelections.Clear();
        foreach (var (game, selection) in settings.Planets) _planetSelections[game] = selection;

        foreach (var (game, config) in _manipConfigs)
        {
            if (settings.Manips.ContainsKey(game)) continue;
            config.Selected = ManipList.None;
            config.AutoReset = true;
            config.Remember = false;
        }

        foreach (var (game, (selected, autoReset, remember)) in settings.Manips)
        {
            if (!_manipConfigs.TryGetValue(game, out var config))
            {
                config = new ManipConfig { Generation = _session.Generation };
                _manipConfigs[game] = config;
            }

            config.Selected = selected;
            config.AutoReset = autoReset;
            config.Remember = remember;
        }
    }

    private static Dictionary<byte, byte[]?[]> ClonePositions(Dictionary<byte, byte[]?[]> positions) =>
        positions.ToDictionary(kv => kv.Key, kv => kv.Value.Select(b => (byte[]?)b?.Clone()).ToArray());
}
