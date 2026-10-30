using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Plugins;
using VRage.Utils;

namespace WheelFrictionFix
{
    /// <summary>
    /// WheelFrictionFix: wheels on blocks behave the same as on voxels.
    ///
    /// Adds a "Voxel-like friction on blocks" checkbox to the wheel suspension terminal
    /// (on by default). For wheels on suspensions with the checkbox on:
    ///  1. The contact friction against any block (armor, conveyors, etc.) equals the wheel
    ///     friction, as on voxels. Vanilla gives 7 % on armor and 100 % on functional blocks.
    ///  2. When the wheel friction changes noticeably (e.g. a script zeroes it for a tank turn),
    ///     the wheel contacts are recreated. Otherwise Havok keeps the old contact points with the
    ///     old friction, and a vehicle standing on blocks cannot turn in place.
    ///
    /// The checkbox state is stored in the suspension block and saved with the world.
    ///
    /// Multiplayer: the server (or host) is authoritative. A client sends checkbox changes to the
    /// server, the server checks block access, stores the state and broadcasts it to everyone.
    /// On join a client asks the server for the list of disabled suspensions. Friction and contact
    /// recreation run on the server and on a client that predicts its vehicle physics.
    /// The plugin only needs to be installed on the host/server; clients need it to see the checkbox.
    /// </summary>
    public class Plugin : IPlugin
    {
        private const string Name = "WheelFrictionFix";

        // Same as on voxels: contact friction = wheel friction, "collector" for the vanilla
        // anti-drift steering assist = 1 (common voxel materials are about 1).
        private const float BlockFrictionMultiplier = 1f;
        private const float BlockFrictionCollector = 1f;

        // Recreate contacts only when friction changes by more than 15 % or to/from 0,
        // and at most 4 times per second per wheel.
        private const float SignificantChange = 0.15f;
        private const double RefreshMinInterval = 0.25;

        // The checkbox state is kept in the suspension's mod storage and saved with the block.
        // Value "1" (on) or "0" (off); no entry = on.
        // The game only saves "claimed" keys (MyModStorageComponent.Serialize), so the plugin
        // claims its key right before saving (SerializePrefix).
        private static readonly Guid StorageKey = new Guid("5b1f3c5e-7a4d-4a3b-9d63-2d5f0e6a8c11");

        private static readonly List<string> InitErrors = new List<string>();
        private static readonly ConcurrentDictionary<string, bool> LoggedErrors = new ConcurrentDictionary<string, bool>();

        // Suspension EntityId -> checkbox. Cached so the physics thread does not read the storage.
        private static readonly ConcurrentDictionary<long, bool> Enabled = new ConcurrentDictionary<long, bool>();
        private static Type storageComponentType;

        // Contact recreation state (per suspension EntityId).
        private static readonly ConcurrentDictionary<long, float> LastFriction = new ConcurrentDictionary<long, float>();
        private static readonly ConcurrentDictionary<long, DateTime> LastRefresh = new ConcurrentDictionary<long, DateTime>();
        private static readonly ConcurrentDictionary<long, PendingRefresh> Pending = new ConcurrentDictionary<long, PendingRefresh>();
        private static readonly ConcurrentDictionary<long, DisabledWheel> Disabled = new ConcurrentDictionary<long, DisabledWheel>();

        private static MethodInfo refreshCollisionFilter;
        private static int? noCollisionLayer;

        // Network channel of the plugin (arbitrary, must not clash with other mods/plugins).
        private const ushort NetChannel = 52817;
        private const byte MsgSet = 1;         // client -> server: suspension id, on/off
        private const byte MsgState = 2;       // server -> clients: suspension id, on/off
        private const byte MsgRequestAll = 3;  // client -> server: send all disabled suspensions
        private const byte MsgAllDisabled = 4; // server -> client: list of disabled suspensions

        private static readonly Action<ushort, byte[], ulong, bool> NetHandler = OnMessage;
        private static IMyMultiplayer registeredMultiplayer;

        private Harmony harmony;
        private bool controlsDone;
        private bool chatShown;
        private bool stateRequested;
        private object lastSession;

        private sealed class PendingRefresh
        {
            public object WheelPhysics;
            public float Friction;
            public DateTime Due;
        }

        private sealed class DisabledWheel
        {
            public object WheelPhysics;
            public object OldLayer;
            public int Frames;
        }

        // ================= Plugin lifecycle =================

        public void Init(object gameInstance)
        {
            harmony = new Harmony(Name);

            try
            {
                var wheelType = AccessTools.TypeByName("Sandbox.Game.Entities.Blocks.MyWheel");
                var contact = wheelType == null ? null : AccessTools.Method(wheelType, "ContactPointCallback");
                if (contact == null)
                    InitErrors.Add("MyWheel.ContactPointCallback not found");
                else
                    harmony.Patch(contact, postfix: new HarmonyMethod(typeof(Plugin), nameof(ContactPostfix)));

                var suspensionType = AccessTools.TypeByName("Sandbox.Game.Entities.Cube.MyMotorSuspension");
                var propagate = suspensionType == null ? null : AccessTools.Method(suspensionType, "PropagateFriction");
                if (propagate == null)
                    InitErrors.Add("MyMotorSuspension.PropagateFriction not found");
                else
                    harmony.Patch(propagate, postfix: new HarmonyMethod(typeof(Plugin), nameof(PropagateFrictionPostfix)));

                var storageType = AccessTools.TypeByName("Sandbox.Game.EntityComponents.MyModStorageComponent");
                var serialize = storageType == null ? null : AccessTools.Method(storageType, "Serialize");
                if (serialize == null)
                    InitErrors.Add("MyModStorageComponent.Serialize not found (toggle state will not be saved)");
                else
                    harmony.Patch(serialize, prefix: new HarmonyMethod(typeof(Plugin), nameof(SerializePrefix)));

                var physicsType = AccessTools.TypeByName("Sandbox.Engine.Physics.MyPhysics");
                refreshCollisionFilter = physicsType == null
                    ? null
                    : AccessTools.GetDeclaredMethods(physicsType)
                        .FirstOrDefault(m => m.Name == "RefreshCollisionFilter" && m.IsStatic && m.GetParameters().Length == 1);
                if (refreshCollisionFilter == null)
                    InitErrors.Add("MyPhysics.RefreshCollisionFilter not found");

                var layers = AccessTools.TypeByName("Sandbox.Engine.Physics.MyPhysics+CollisionLayers");
                var layerField = layers == null
                    ? null
                    : layers.GetField("NoCollisionLayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (layerField != null)
                    noCollisionLayer = Convert.ToInt32(layerField.GetValue(null), CultureInfo.InvariantCulture);
                else
                    InitErrors.Add("MyPhysics.CollisionLayers.NoCollisionLayer not found");
            }
            catch (Exception e)
            {
                InitErrors.Add("init failed: " + e);
            }

            MyLog.Default.WriteLine(InitErrors.Count == 0
                ? "[" + Name + "] initialized"
                : "[" + Name + "] initialized with errors: " + string.Join("; ", InitErrors));
        }

        public void Update()
        {
            var session = MyAPIGateway.Session;
            if (!ReferenceEquals(session, lastSession))
            {
                lastSession = session;
                chatShown = false;
                stateRequested = false;
                UnregisterNetwork();
                // The game removes added controls when a world unloads: add them again for the new world.
                controlsDone = false;
                Enabled.Clear();
                LastFriction.Clear();
                LastRefresh.Clear();
                Pending.Clear();
                Disabled.Clear();
            }

            if (session != null && registeredMultiplayer == null && MyAPIGateway.Multiplayer != null)
                RegisterNetwork();

            // Multiplayer client: ask the server once for the disabled suspensions.
            var mp = MyAPIGateway.Multiplayer;
            if (!stateRequested && session != null && session.Player != null && mp != null
                && mp.MultiplayerActive && !mp.IsServer)
            {
                stateRequested = true;
                Send(new[] { MsgRequestAll }, toServer: true);
            }

            if (!chatShown && session != null && session.Player != null && MyAPIGateway.Utilities != null)
            {
                chatShown = true;
                MyAPIGateway.Utilities.ShowMessage(Name, InitErrors.Count == 0
                    ? "plugin initialized"
                    : "plugin initialized with errors, see the game log");
            }

            // First restore collisions for wheels disabled last frame, then disable new ones.
            if (!Disabled.IsEmpty)
                RestoreDisabledWheels();
            if (!Pending.IsEmpty)
                ProcessPending();

            // No terminal on a dedicated server: controls are not needed.
            if (!controlsDone && session != null && MyAPIGateway.TerminalControls != null
                && MyAPIGateway.Utilities != null && !MyAPIGateway.Utilities.IsDedicated)
            {
                controlsDone = true;
                try
                {
                    AddControls();
                }
                catch (Exception e)
                {
                    MyLog.Default.WriteLine("[" + Name + "] failed to add terminal controls: " + e);
                }
            }
        }

        public void Dispose()
        {
            UnregisterNetwork();
            harmony?.UnpatchAll(Name);
        }

        // ================= Terminal checkbox =================

        private static void AddControls()
        {
            var controls = MyAPIGateway.TerminalControls;

            // Guard against a duplicate in case the control survived a world reload.
            List<IMyTerminalControl> existing;
            controls.GetControls<IMyMotorSuspension>(out existing);
            if (existing != null && existing.Any(c => c.Id == Name + "_Enabled"))
                return;

            var checkbox = controls.CreateControl<IMyTerminalControlCheckbox, IMyMotorSuspension>(Name + "_Enabled");
            checkbox.Title = MyStringId.GetOrCompute("Voxel-like friction on blocks");
            checkbox.Tooltip = MyStringId.GetOrCompute(
                "Wheels on this suspension get the same friction on blocks as on voxels, " +
                "and react to friction changes immediately (fixes tank turns on blocks). Off = vanilla.");
            checkbox.OnText = MyStringId.GetOrCompute("On");
            checkbox.OffText = MyStringId.GetOrCompute("Off");
            checkbox.SupportsMultipleBlocks = true;
            checkbox.Visible = b => true;
            checkbox.Enabled = b => true;
            checkbox.Getter = b => IsOn(b);
            checkbox.Setter = (b, value) => RequestSet(b as IMyMotorSuspension, value);
            controls.AddControl<IMyMotorSuspension>(checkbox);
        }

        private static bool IsOn(IMyEntity suspension)
        {
            bool on;
            if (Enabled.TryGetValue(suspension.EntityId, out on))
                return on;

            // No entry -> on by default.
            string value;
            var storage = suspension.Storage;
            on = !(storage != null && storage.TryGetValue(StorageKey, out value)
                   && value != null && value.StartsWith("0", StringComparison.Ordinal));
            Enabled[suspension.EntityId] = on;
            return on;
        }

        private static void SetOn(IMyEntity suspension, bool on)
        {
            if (suspension.Storage == null)
            {
                if (storageComponentType == null)
                    storageComponentType = AccessTools.TypeByName("Sandbox.Game.EntityComponents.MyModStorageComponent");
                if (storageComponentType == null)
                    throw new InvalidOperationException("MyModStorageComponent type not found");

                typeof(IMyEntity).GetProperty("Storage").SetValue(suspension, Activator.CreateInstance(storageComponentType), null);
            }

            suspension.Storage.SetValue(StorageKey, on ? "1" : "0");
            Enabled[suspension.EntityId] = on;
        }

        /// <summary>
        /// Before MyModStorageComponent.Serialize: claim the plugin key, otherwise the game does not
        /// save it ("Not saving ModStorageComponent GUID ..., not claimed").
        /// The list is cleared at the end of every Serialize, so the key is added every time.
        /// </summary>
        public static void SerializePrefix(HashSet<Guid> ___m_cachedGuids)
        {
            if (___m_cachedGuids != null)
                ___m_cachedGuids.Add(StorageKey);
        }

        // ================= Network =================

        /// <summary>
        /// The checkbox was toggled in the terminal. Singleplayer and the server/host apply it
        /// immediately (the server also broadcasts it); a client applies it locally and asks the server.
        /// </summary>
        private static void RequestSet(IMyMotorSuspension suspension, bool on)
        {
            if (suspension == null)
                return;

            ApplyState(suspension, on);

            var mp = MyAPIGateway.Multiplayer;
            if (mp == null || !mp.MultiplayerActive)
                return;

            var message = EncodeState(mp.IsServer ? MsgState : MsgSet, suspension.EntityId, on);
            Send(message, toServer: !mp.IsServer);
        }

        /// <summary>Applies the state: block storage, cache and an immediate contact recreation.</summary>
        private static void ApplyState(IMyMotorSuspension suspension, bool on)
        {
            try
            {
                SetOn(suspension, on);
                RefreshNow(suspension);
            }
            catch (Exception e)
            {
                LogOnce("apply state", e);
            }
        }

        private static void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            try
            {
                if (data == null || data.Length == 0)
                    return;

                var mp = MyAPIGateway.Multiplayer;
                bool isServer = mp != null && mp.IsServer;

                switch (data[0])
                {
                    case MsgSet:
                    {
                        if (!isServer || data.Length < 10)
                            return;

                        long id = BitConverter.ToInt64(data, 1);
                        bool on = data[9] != 0;
                        var suspension = MyAPIGateway.Entities.GetEntityById(id) as IMyMotorSuspension;
                        if (suspension == null || !HasAccess(suspension, sender))
                        {
                            // Denied: send the current state back so the client's checkbox reverts.
                            if (suspension != null)
                                mp.SendMessageTo(NetChannel, EncodeState(MsgState, id, IsOn(suspension)), sender);
                            return;
                        }

                        ApplyState(suspension, on);
                        mp.SendMessageToOthers(NetChannel, EncodeState(MsgState, id, on));
                        return;
                    }

                    case MsgState:
                    {
                        if (!fromServer || isServer || data.Length < 10)
                            return;

                        long id = BitConverter.ToInt64(data, 1);
                        bool on = data[9] != 0;
                        var suspension = MyAPIGateway.Entities.GetEntityById(id) as IMyMotorSuspension;
                        if (suspension != null)
                            ApplyState(suspension, on);
                        else
                            Enabled[id] = on; // block not streamed to this client yet
                        return;
                    }

                    case MsgRequestAll:
                    {
                        if (!isServer)
                            return;

                        var ids = CollectDisabledSuspensions();
                        var reply = new byte[1 + 4 + ids.Count * 8];
                        reply[0] = MsgAllDisabled;
                        BitConverter.GetBytes(ids.Count).CopyTo(reply, 1);
                        for (int i = 0; i < ids.Count; i++)
                            BitConverter.GetBytes(ids[i]).CopyTo(reply, 5 + i * 8);
                        mp.SendMessageTo(NetChannel, reply, sender);
                        return;
                    }

                    case MsgAllDisabled:
                    {
                        if (!fromServer || isServer || data.Length < 5)
                            return;

                        int count = BitConverter.ToInt32(data, 1);
                        for (int i = 0; i < count && 5 + i * 8 + 8 <= data.Length; i++)
                        {
                            long id = BitConverter.ToInt64(data, 5 + i * 8);
                            var suspension = MyAPIGateway.Entities.GetEntityById(id) as IMyMotorSuspension;
                            if (suspension != null)
                                ApplyState(suspension, false);
                            else
                                Enabled[id] = false;
                        }
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce("network message", e);
            }
        }

        /// <summary>Whether the player has access to the block (same as its terminal).</summary>
        private static bool HasAccess(IMyMotorSuspension suspension, ulong steamId)
        {
            try
            {
                long identityId = MyAPIGateway.Players.TryGetIdentityId(steamId);
                return identityId != 0 && suspension.HasPlayerAccess(identityId);
            }
            catch (Exception e)
            {
                LogOnce("access check", e);
                return false;
            }
        }

        /// <summary>All suspensions in the world with the checkbox off (server side).</summary>
        private static List<long> CollectDisabledSuspensions()
        {
            var result = new List<long>();
            var grids = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(grids, e => e is IMyCubeGrid);

            var blocks = new List<IMySlimBlock>();
            foreach (IMyCubeGrid grid in grids)
            {
                blocks.Clear();
                grid.GetBlocks(blocks, b => b.FatBlock is IMyMotorSuspension);
                foreach (var block in blocks)
                {
                    if (!IsOn(block.FatBlock))
                        result.Add(block.FatBlock.EntityId);
                }
            }
            return result;
        }

        private static byte[] EncodeState(byte type, long id, bool on)
        {
            var data = new byte[10];
            data[0] = type;
            BitConverter.GetBytes(id).CopyTo(data, 1);
            data[9] = (byte)(on ? 1 : 0);
            return data;
        }

        private static void Send(byte[] data, bool toServer)
        {
            var mp = MyAPIGateway.Multiplayer;
            if (mp == null || !mp.MultiplayerActive)
                return;

            if (toServer)
                mp.SendMessageToServer(NetChannel, data);
            else
                mp.SendMessageToOthers(NetChannel, data);
        }

        private static void RegisterNetwork()
        {
            try
            {
                registeredMultiplayer = MyAPIGateway.Multiplayer;
                registeredMultiplayer.RegisterSecureMessageHandler(NetChannel, NetHandler);
            }
            catch (Exception e)
            {
                LogOnce("register network", e);
            }
        }

        private static void UnregisterNetwork()
        {
            if (registeredMultiplayer == null)
                return;

            try
            {
                registeredMultiplayer.UnregisterSecureMessageHandler(NetChannel, NetHandler);
            }
            catch
            {
                // The world is already unloaded: the handler was removed with it.
            }
            registeredMultiplayer = null;
        }

        // ================= Contact friction =================

        /// <summary>
        /// After MyWheel.ContactPointCallback: if the wheel touches a grid, set the contact friction
        /// as on voxels (vanilla uses 7 % on armor and 100 % on other blocks).
        /// </summary>
        public static void ContactPostfix(object __instance, object[] __args)
        {
            try
            {
                var top = __instance as IMyAttachableTopBlock;
                var suspension = top == null ? null : top.Base as IMyMotorSuspension;
                if (suspension == null)
                    return;

                object info = __args[0];
                if (!(GetMember(info, "CollidingEntity") is IMyCubeGrid))
                    return;
                if (!IsOn(suspension))
                    return;

                float wheelFriction = Convert.ToSingle(GetMember(__instance, "Friction"), CultureInfo.InvariantCulture);
                object properties = GetMember(GetMember(info, "Event"), "ContactProperties");
                SetMember(properties, "Friction", wheelFriction * BlockFrictionMultiplier);
                SetMember(__instance, "m_frictionCollector", BlockFrictionCollector);
            }
            catch (Exception e)
            {
                LogOnce("contact", e);
            }
        }

        // ================= Contact recreation =================

        /// <summary>
        /// After MyMotorSuspension.PropagateFriction (wheel friction changed): on a noticeable
        /// change, schedule a recreation of the wheel contacts.
        /// </summary>
        public static void PropagateFrictionPostfix(object __instance)
        {
            try
            {
                var suspension = __instance as IMyMotorSuspension;
                if (suspension == null || !IsOn(suspension))
                    return;

                object physics;
                float friction;
                if (!TryGetWheel(suspension, out physics, out friction))
                    return;

                long id = suspension.EntityId;
                float last;
                if (!LastFriction.TryGetValue(id, out last))
                {
                    LastFriction[id] = friction;
                    return;
                }

                if (IsSignificantChange(last, friction))
                    Schedule(id, physics, friction);
            }
            catch (Exception e)
            {
                LogOnce("propagate", e);
            }
        }

        private static void RefreshNow(IMyMotorSuspension suspension)
        {
            try
            {
                object physics;
                float friction;
                if (suspension != null && TryGetWheel(suspension, out physics, out friction))
                    Schedule(suspension.EntityId, physics, friction);
            }
            catch (Exception e)
            {
                LogOnce("refresh-now", e);
            }
        }

        private static bool TryGetWheel(IMyMotorSuspension suspension, out object physics, out float friction)
        {
            physics = null;
            friction = 0f;

            object wheel = GetMember(suspension, "TopBlock");
            if (wheel == null || FindProperty(wheel.GetType(), "Friction") == null)
                return false;

            physics = GetMember(GetMember(wheel, "CubeGrid"), "Physics");
            if (physics == null)
                return false;

            friction = Convert.ToSingle(GetMember(wheel, "Friction"), CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsSignificantChange(float from, float to)
        {
            if ((from <= 1e-3f) != (to <= 1e-3f))
                return true;
            float max = Math.Max(Math.Abs(from), Math.Abs(to));
            return max > 1e-3f && Math.Abs(from - to) / max > SignificantChange;
        }

        private static void Schedule(long id, object physics, float friction)
        {
            DateTime last;
            var due = LastRefresh.TryGetValue(id, out last) ? last.AddSeconds(RefreshMinInterval) : DateTime.MinValue;
            Pending.AddOrUpdate(id,
                new PendingRefresh { WheelPhysics = physics, Friction = friction, Due = due },
                (key, existing) =>
                {
                    existing.WheelPhysics = physics;
                    existing.Friction = friction;
                    return existing;
                });
        }

        /// <summary>
        /// Phase 1 (main thread): queued wheels stop colliding. On the next physics step
        /// Havok removes their old contact points.
        /// </summary>
        private static void ProcessPending()
        {
            if (refreshCollisionFilter == null || !noCollisionLayer.HasValue)
            {
                Pending.Clear();
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var pair in Pending.ToArray())
            {
                // The wheel is still disabled from last time, or it is too early: wait.
                if (pair.Value.Due > now || Disabled.ContainsKey(pair.Key))
                    continue;

                PendingRefresh item;
                if (!Pending.TryRemove(pair.Key, out item))
                    continue;

                try
                {
                    object body = GetMember(item.WheelPhysics, "RigidBody");
                    if (body == null)
                        continue;

                    object oldLayer = GetMember(body, "Layer");
                    SetMember(body, "Layer", noCollisionLayer.Value);
                    refreshCollisionFilter.Invoke(null, new[] { item.WheelPhysics });
                    Disabled[pair.Key] = new DisabledWheel { WheelPhysics = item.WheelPhysics, OldLayer = oldLayer };

                    LastFriction[pair.Key] = item.Friction;
                    LastRefresh[pair.Key] = now;
                }
                catch (Exception e)
                {
                    LogOnce("disable", e);
                }
            }
        }

        /// <summary>
        /// Phase 2 (next frame): restore the wheel layer; contacts are created again and go
        /// through the contact callback with the current friction.
        /// </summary>
        private static void RestoreDisabledWheels()
        {
            foreach (var pair in Disabled.ToArray())
            {
                // Wait for at least one physics step between disabling and restoring.
                if (++pair.Value.Frames < 2)
                    continue;

                DisabledWheel item;
                if (!Disabled.TryRemove(pair.Key, out item))
                    continue;

                try
                {
                    object body = GetMember(item.WheelPhysics, "RigidBody");
                    if (body == null)
                        continue;

                    SetMember(body, "Layer", item.OldLayer);
                    refreshCollisionFilter.Invoke(null, new[] { item.WheelPhysics });
                }
                catch (Exception e)
                {
                    LogOnce("restore", e);
                }
            }
        }

        // ================= Reflection =================

        private const BindingFlags DeclaredMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Walk the hierarchy with DeclaredOnly: some game types hide members with "new",
        // and a plain GetProperty on them throws AmbiguousMatchException.
        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var property = t.GetProperty(name, DeclaredMembers);
                if (property != null)
                    return property;
            }
            return null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, DeclaredMembers);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
                return null;

            var type = obj.GetType();
            var property = FindProperty(type, name);
            if (property != null)
                return property.GetValue(obj, null);

            var field = FindField(type, name);
            if (field != null)
                return field.GetValue(obj);

            throw new MissingMemberException(type.FullName, name);
        }

        private static void SetMember(object obj, string name, object value)
        {
            var type = obj.GetType();
            var property = FindProperty(type, name);
            if (property != null)
            {
                property.SetValue(obj, ConvertTo(value, property.PropertyType), null);
                return;
            }

            var field = FindField(type, name);
            if (field != null)
            {
                field.SetValue(obj, ConvertTo(value, field.FieldType));
                return;
            }

            throw new MissingMemberException(type.FullName, name);
        }

        private static object ConvertTo(object value, Type target)
        {
            if (value == null || target.IsInstanceOfType(value))
                return value;
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        private static void LogOnce(string where, Exception e)
        {
            if (LoggedErrors.TryAdd(where + "|" + e.GetType().Name + "|" + e.Message, true))
                MyLog.Default.WriteLine("[" + Name + "] " + where + " failed: " + e);
        }
    }
}
