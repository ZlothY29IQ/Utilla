using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using GorillaGameModes;
using GorillaLibrary.Attributes;
using GorillaLibrary.Extensions;
using GorillaLibrary.Models;
using GorillaLibrary.Utilities;
using GorillaLocomotion;
using GorillaNetworking;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using Photon.Realtime;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;
using Utilla;
using Object = UnityEngine.Object;

namespace Utilla
{
    [BepInPlugin("dev.gorillalibrary", "GorillaLibrary", "1.0.3")]
    public class GorillaLibraryCompat : BaseUnityPlugin
    {
        private static readonly List<RegisteredPlugin> RegisteredPlugins = new();

        private static bool                 resolverHooked;
        private static bool                 initialized;
        private static string               currentGamemode;
        public static  GorillaLibraryCompat Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            HookAssemblyResolver();
        }

        private static void HookAssemblyResolver()
        {
            if (resolverHooked)
                return;

            resolverHooked = true;

            AppDomain.CurrentDomain.AssemblyResolve += ResolveGorillaLibraryAssembly;
        }

        private static Assembly ResolveGorillaLibraryAssembly(object sender, ResolveEventArgs args)
        {
            string requestedName = new AssemblyName(args.Name).Name;

            return requestedName == "GorillaLibrary" ? typeof(GorillaLibraryCompat).Assembly : null;
        }

        public static void LogError(object message)
        {
            if (Instance != null)
                Instance.Logger.LogError(message);
        }

        public static void LogInfo(object message)
        {
            if (Instance != null)
                Instance.Logger.LogInfo(message);
        }

        public static void RefreshPluginCache()
        {
            RegisteredPlugins.Clear();

            foreach (KeyValuePair<string, BepInEx.PluginInfo> pair in Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo pluginInfo = pair.Value;

                if (pluginInfo == null || pluginInfo.Instance == null)
                    continue;

                BaseUnityPlugin plugin = pluginInfo.Instance;

                if (plugin == Instance)
                    continue;

                Type pluginType = plugin.GetType();

                ModdedGamemodeAttribute[] gamemodeAttributes = pluginType
                                                              .GetCustomAttributes(typeof(ModdedGamemodeAttribute),
                                                                       true)
                                                              .Cast<ModdedGamemodeAttribute>()
                                                              .ToArray();

                if (gamemodeAttributes.Length == 0)
                    continue;

                RegisteredPlugin registeredPlugin = new()
                {
                        Plugin            = plugin,
                        AnyModdedGamemode = false,
                        GamemodeIds       = new List<string>(),
                        JoinMethods       = GetMarkedMethods(pluginType, typeof(ModdedGamemodeJoinAttribute)),
                        LeaveMethods      = GetMarkedMethods(pluginType, typeof(ModdedGamemodeLeaveAttribute)),
                };

                foreach (ModdedGamemodeAttribute attribute in gamemodeAttributes)
                {
                    if (attribute.gamemode == null)
                    {
                        registeredPlugin.AnyModdedGamemode = true;

                        continue;
                    }

                    if (!string.IsNullOrEmpty(attribute.gamemode.ID))
                    {
                        registeredPlugin.GamemodeIds.Add(attribute.gamemode.ID);
                        GameModeUtility.RegisterGameMode(attribute.gamemode);
                    }
                }

                RegisteredPlugins.Add(registeredPlugin);
            }
        }

        public static void InvokeGameInitialized()
        {
            if (initialized)
                return;

            initialized = true;

            RefreshPluginCache();

            CoreUtility.MarkInitialized();
            RigUtility.Initialize();
            InputUtility.Initialize();
            GorillaLibrary.Events.Core.OnGameInitialized?.Invoke();
        }

        public static void InvokeRoomJoined(string gamemode)
        {
            currentGamemode = gamemode ?? string.Empty;
            GameModeUtility.SetCurrentGameMode(currentGamemode);

            GorillaLibrary.Events.Room.OnRoomJoined?.Invoke();

            foreach (RegisteredPlugin plugin in RegisteredPlugins.Where(plugin => MatchesGamemode(plugin,
                                                                                currentGamemode)))
                InvokeMarkedMethods(plugin.Plugin, plugin.JoinMethods, currentGamemode);
        }

        public static void InvokeRoomLeft() => InvokeRoomLeft(currentGamemode);

        public static void InvokeRoomLeft(string gamemode)
        {
            gamemode = gamemode ?? currentGamemode ?? string.Empty;

            GorillaLibrary.Events.Room.OnRoomLeft?.Invoke();

            foreach (RegisteredPlugin plugin in RegisteredPlugins.Where(plugin => MatchesGamemode(plugin, gamemode)))
                InvokeMarkedMethods(plugin.Plugin, plugin.LeaveMethods, gamemode);

            currentGamemode = string.Empty;
            GameModeUtility.SetCurrentGameMode(null);
        }

        public static void InvokePlayerEnteredRoom(NetPlayer player) => GorillaLibrary.Events.Player.OnPlayerEnteredRoom?.Invoke(player);

        public static void InvokePlayerLeftRoom(NetPlayer player) => GorillaLibrary.Events.Player.OnPlayerLeftRoom?.Invoke(player);

        public static void InvokePlayerNameChanged(NetPlayer player, string name) => GorillaLibrary.Events.Player.OnPlayerNameChanged?.Invoke(player, name);

        public static void InvokeRigAdded(VRRig rig, NetPlayer player) => GorillaLibrary.Events.Rig.OnRigAdded?.Invoke(rig, player);

        public static void InvokeRigRemoved(VRRig rig) => GorillaLibrary.Events.Rig.OnRigRemoved?.Invoke(rig);

        public static void InvokeRigColourChanged(VRRig rig, Color colour) => GorillaLibrary.Events.Rig.OnColourChanged?.Invoke(rig, colour);

        public static void InvokeGameOverlayActivation(bool active) => GorillaLibrary.Events.Player.OnGameOverlayActivation?.Invoke(active);

        private static List<MethodInfo> GetMarkedMethods(Type type, Type attributeType)
        {
            List<MethodInfo> methods = new();

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (MethodInfo method in type.GetMethods(Flags))
            {
                if (method.GetCustomAttributes(attributeType, true).Length == 0)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();

                switch (parameters.Length)
                {
                    case 0:
                        methods.Add(method);

                        continue;

                    case 1 when parameters[0].ParameterType == typeof(string):
                        methods.Add(method);

                        break;
                }
            }

            return methods;
        }

        private static void InvokeMarkedMethods(BaseUnityPlugin plugin, List<MethodInfo> methods, string gamemode)
        {
            foreach (MethodInfo method in methods)
                try
                {
                    ParameterInfo[] parameters = method.GetParameters();

                    if (parameters.Length == 0)
                    {
                        method.Invoke(plugin, null);

                        continue;
                    }

                    method.Invoke(plugin, new object[] { gamemode, });
                }
                catch (Exception ex)
                {
                    Instance?.Logger.LogError("Failed to invoke GorillaLibrary compatibility method " + method.Name);
                    Instance?.Logger.LogError(ex);
                }
        }

        private static bool MatchesGamemode(RegisteredPlugin plugin, string gamemode)
        {
            if (string.IsNullOrEmpty(gamemode))
                return false;

            if (plugin.AnyModdedGamemode && gamemode.Contains(GorillaLibrary.Constants.ModdedPrefix))
                return true;

            return plugin.GamemodeIds.Any(id => !string.IsNullOrEmpty(id) && gamemode.Contains(id));
        }

        private sealed class RegisteredPlugin
        {
            public bool             AnyModdedGamemode;
            public List<string>     GamemodeIds;
            public List<MethodInfo> JoinMethods;
            public List<MethodInfo> LeaveMethods;
            public BaseUnityPlugin  Plugin;
        }
    }
}

namespace GorillaLibrary
{
    public class Constants
    {
        public const string ModdedPrefix = "MODDED_";
    }

    public class Events
    {
        public static readonly CoreEvents     Core      = new();
        public static readonly PlayerEvents   Player    = new();
        public static readonly RigEvents      Rig       = new();
        public static readonly RoomEvents     Room      = new();
        public static readonly ServerEvents   Server    = new();
        public static readonly ZoneEvents     Zone      = new();
        public static readonly GameModeEvents GameMode  = new();
        public static readonly CosmeticEvents Cosmetics = new();
    }

    public class CoreEvents
    {
        public Action OnGameInitialized;
    }

    public class PlayerEvents
    {
        public Action<bool>              OnGameOverlayActivation;
        public Action<NetPlayer>         OnPlayerEnteredRoom;
        public Action<NetPlayer>         OnPlayerLeftRoom;
        public Action<NetPlayer, string> OnPlayerNameChanged;
    }

    public class RigEvents
    {
        public Action<VRRig, Color>     OnColourChanged;
        public Action<VRRig, NetPlayer> OnRigAdded;
        public Action<VRRig>            OnRigRemoved;
    }

    public class RoomEvents
    {
        public Action OnRoomJoined;
        public Action OnRoomLeft;
    }

    public class ServerEvents
    {
        public Action<string, string> OnMothershipMessageRecieved;
    }

    public class ZoneEvents
    {
        public Action<IEnumerable<GTZone>> OnZonesChanged;
    }

    public class GameModeEvents
    {
        public Action<GorillaGameManager, NetPlayer, NetPlayer> OnPlayerTagged;
        public Action<GorillaGameManager>                       OnRoundCompleted;
    }

    public class CosmeticEvents
    {
        public Action OnWornCosmeticsUpdated;
    }

    public class GorillaUnityPlugin : BaseUnityPlugin
    {
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public bool IsStateSupported => true;
    }
}

namespace GorillaLibrary.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ModdedGamemodeAttribute : Attribute
    {
        public readonly GameModeWrapper gamemode;

        public ModdedGamemodeAttribute() => gamemode = null;

        public ModdedGamemodeAttribute(string       id, string displayName,
                                       GameModeType gameModeType = GameModeType.Infection) =>
                gamemode = new GameModeWrapper(id, displayName, gameModeType);

        public ModdedGamemodeAttribute(string id, string displayName, Type gameManager) =>
                gamemode = new GameModeWrapper(id, displayName, gameManager);
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ModdedGamemodeJoinAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class ModdedGamemodeLeaveAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ModdedWardrobeSectionAttribute : Attribute
    {
        public Type[] SectionTypes;
        public string Title;

        public ModdedWardrobeSectionAttribute(string title, params Type[] types)
        {
            Title        = title;
            SectionTypes = types;
        }
    }
}

namespace GorillaLibrary.Models
{
    public class GameModeWrapper
    {
        internal GameModeWrapper(GameModeType gameModeType)
        {
            BaseGameMode = gameModeType;
            ID           = gameModeType.ToString();
            DisplayName  = gameModeType.ToString();
        }

        public GameModeWrapper(string id, string displayName, GameModeType? gameModeType = null)
        {
            BaseGameMode = gameModeType;
            string baseModeName = gameModeType != null ? gameModeType.ToString() : string.Empty;
            ID          = !string.IsNullOrEmpty(baseModeName) && !id.Contains(baseModeName) ? id + baseModeName : id;
            DisplayName = displayName;
        }

        public GameModeWrapper(string id, string displayName, Type gameManager)
        {
            ID          = id;
            DisplayName = displayName;
            GameManager = gameManager;
        }

        public string        DisplayName  { get; private set; }
        public string        ID           { get; }
        public GameModeType? BaseGameMode { get; }
        public Type          GameManager  { get; private set; }

        public string GameModeName => BaseGameMode != null ? BaseGameMode.ToString() : ID;
    }
}

namespace GorillaLibrary.Utilities
{
    public static class CoreUtility
    {
        public static bool   Initialized { get; private set; }
        public static string Platform    { get; private set; }
        public static bool   IsSteam     => Platform == "Steam";

        public static void MarkInitialized()
        {
            Initialized = true;

            try
            {
                Platform = PlayFabAuthenticator.instance.platform.ToString();
            }
            catch
            {
                Platform = "Unknown";
            }
        }
    }
}

namespace GorillaLibrary.Models
{
    public enum GorillaRigBone
    {
        Head,
        Body,
        LeftUpperArm,
        LeftLowerArm,
        LeftHand,
        RightUpperArm,
        RightLowerArm,
        RightHand,
    }

    public abstract class InputTracker
    {
        public string               name;
        public XRNode               node;
        public Action<InputTracker> OnPressed;
        public Action<InputTracker> OnReleased;
        public bool                 pressed;
        public Quaternion           quaternionValue;
        public Traverse             traverse;
        public Vector3              vector3Value;
        public bool                 wasPressed;

        public abstract void UpdateValues();
    }

    public class InputTracker<T> : InputTracker
    {
        public InputTracker(Traverse traverse, XRNode node)
        {
            this.traverse = traverse;
            this.node     = node;
        }

        public T GetValue() => traverse.GetValue<T>();

        public override void UpdateValues()
        {
            wasPressed = pressed;

            try
            {
                if (typeof(T) == typeof(bool))
                {
                    pressed = traverse.GetValue<bool>();
                }
                else if (typeof(T) == typeof(float))
                {
                    pressed = traverse.GetValue<float>() > 0.5f;
                }
                else if (typeof(T) == typeof(Vector2))
                {
                    object value = traverse.GetValue();
                    if (value is Vector2 v2)
                        vector3Value = new Vector3(v2.x, v2.y, 0f);
                }
                else if (typeof(T) == typeof(Vector3))
                {
                    vector3Value = traverse.GetValue<Vector3>();
                }
                else if (typeof(T) == typeof(Quaternion))
                {
                    quaternionValue = traverse.GetValue<Quaternion>();
                }
            }
            catch { }

            if (!wasPressed && pressed)
                OnPressed?.Invoke(this);

            if (wasPressed && !pressed)
                OnReleased?.Invoke(this);
        }
    }

    internal class InternalRoom
    {
        public bool   IsPrivate { get; set; }
        public string Gamemode  { get; set; }
    }

    public enum RequestMethod
    {
        Get,
        Post,
        Put,
        Delete,
        Patch,
        Head,
        Options,
        Trace,
        Connect,
    }

    public class WebRequest
    {
        public RequestMethod              Method      { get; set; }
        public JObject                    PostData    { get; set; }
        public string                     ContentType { get; set; } = "application/json";
        public Dictionary<string, string> Headers     { get; set; }
    }

    public class WebSocketData
    {
        public readonly CancellationTokenSource CancellationSource;

        public readonly ClientWebSocket                           Socket;
        public readonly Dictionary<string, List<Action<JObject>>> Subscribers;

        public WebSocketData(ClientWebSocket socket, CancellationTokenSource cts)
        {
            Socket             = socket;
            CancellationSource = cts;
            Subscribers        = new Dictionary<string, List<Action<JObject>>>();
        }

        public bool IsConnected => Socket != null && Socket.State == WebSocketState.Open;
    }

    public class AssetLoaderSync
    {
        private readonly Assembly                   assembly;
        private readonly Dictionary<string, object> cache = new();
        private readonly string                     path;
        private          AssetBundle                bundle;

        public AssetLoaderSync(string path)
        {
            assembly  = Assembly.GetCallingAssembly();
            this.path = path;
        }

        public AssetLoaderSync(Assembly assembly, string path)
        {
            this.assembly = assembly;
            this.path     = path;
        }

        public AssetBundle GetBundle()
        {
            if (bundle == null)
                bundle = AssetBundleUtility.LoadBundle(assembly, path);

            return bundle;
        }

        public T LoadAsset<T>(string name) where T : Object
        {
            object cachedAsset;

            if (cache.TryGetValue(name, out cachedAsset) && cachedAsset is T typed)
                return typed;

            T asset = GetBundle().LoadAsset<T>(name);
            cache[name] = asset;

            return asset;
        }

        public T[] LoadAssetWithSubAssets<T>(string name) where T : Object
        {
            object cachedAsset;

            if (cache.TryGetValue(name, out cachedAsset) && cachedAsset is T[] typed)
                return typed;

            T[] assets = GetBundle().LoadAssetWithSubAssets<T>(name);
            cache[name] = assets;

            return assets;
        }
    }

    public class AssetLoaderAsync
    {
        private readonly Assembly                   assembly;
        private readonly Dictionary<string, object> cache = new();
        private readonly string                     path;
        private          AssetBundle                bundle;
        private          Task                       bundleLoadTask;
        private          bool                       loaded;

        public AssetLoaderAsync(string path)
        {
            assembly  = Assembly.GetCallingAssembly();
            this.path = path;
        }

        public AssetLoaderAsync(Assembly assembly, string path)
        {
            this.assembly = assembly;
            this.path     = path;
        }

        private async Task LoadBundle()
        {
            if (loaded)
                return;

            bundle = await AssetBundleUtility.LoadBundleAsync(assembly, path);
            loaded = true;
        }

        async public Task<T> LoadAsset<T>(string name) where T : Object
        {
            object cachedAsset;

            if (cache.TryGetValue(name, out cachedAsset) && cachedAsset is T typed)
                return typed;

            if (!loaded)
            {
                if (bundleLoadTask == null)
                    bundleLoadTask = LoadBundle();

                await bundleLoadTask;
            }

            T asset = await AssetBundleUtility.LoadAssetAsync<T>(bundle, name);
            cache[name] = asset;

            return asset;
        }

        async public Task<T[]> LoadAssetWithSubAssets<T>(string name) where T : Object
        {
            object cachedAsset;

            if (cache.TryGetValue(name, out cachedAsset) && cachedAsset is T[] typed)
                return typed;

            if (!loaded)
            {
                if (bundleLoadTask == null)
                    bundleLoadTask = LoadBundle();

                await bundleLoadTask;
            }

            T[] assets = await AssetBundleUtility.LoadAssetsWithSubAssetsAsync<T>(bundle, name);
            cache[name] = assets;

            return assets;
        }
    }
}

namespace GorillaLibrary.Extensions
{
    public static class ObjectExtensions
    {
        public static bool IsObjectExistent(this Object obj) => obj != null && obj;

        public static bool IsObjectNull(this Object obj) => !obj.IsObjectExistent();
    }

    public static class StringExtensions
    {
        private static readonly CultureInfo                Invariant = CultureInfo.InvariantCulture;
        private static readonly TextInfo                   TextInfo  = Invariant.TextInfo;
        private static readonly Dictionary<string, string> NameCache = new();

        public static string SanitizeName(this string name)
        {
            name = name ?? string.Empty;

            string cachedName;

            if (NameCache.TryGetValue(name, out cachedName))
                return cachedName;

            string sanitizedName = name;

            try
            {
                VRRig rig = RigUtility.LocalRig;

                if (rig != null)
                    sanitizedName = rig.NormalizeName(true, name);
            }
            catch
            {
                sanitizedName = name;
            }

            NameCache[name] = sanitizedName;

            return sanitizedName;
        }

        public static string ToTitleCase(this string original, bool forceLower = true)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            return TextInfo.ToTitleCase(forceLower ? original.ToLowerInvariant() : original);
        }

        public static string LimitLength(this string str, int maxLength)
        {
            if (str == null || maxLength < 0)
                return string.Empty;

            return str.Length > maxLength ? str.Substring(0, maxLength) : str;
        }
    }

    public static class TaskExtensions
    {
        private static MonoBehaviour Behaviour
        {
            get
            {
                if (GTPlayer.Instance != null)
                    return GTPlayer.Instance;

                return GorillaLibraryCompat.Instance;
            }
        }

        async public static Task AsAwaitable(this YieldInstruction instruction)
        {
            MonoBehaviour behaviour = Behaviour;

            if (behaviour == null)
                return;

            TaskCompletionSource<YieldInstruction> completionSource = new();
            IEnumerator                            coroutine        = AwaitableRoutine(instruction, completionSource);

            behaviour.StartCoroutine(coroutine);
            await completionSource.Task;
            behaviour.StopCoroutine(coroutine);
        }

        async public static Task AsAwaitable(this CustomYieldInstruction instruction)
        {
            MonoBehaviour behaviour = Behaviour;

            if (behaviour == null)
                return;

            TaskCompletionSource<CustomYieldInstruction> completionSource = new();
            IEnumerator                                  coroutine        = AwaitableRoutine(instruction, completionSource);

            behaviour.StartCoroutine(coroutine);
            await completionSource.Task;
            behaviour.StopCoroutine(coroutine);
        }

        async public static Task AsAwaitable(this AsyncOperation operation)
        {
            if (operation == null)
                return;

            while (!operation.isDone)
                await Task.Yield();
        }

        private static IEnumerator AwaitableRoutine(YieldInstruction                       instruction,
                                                    TaskCompletionSource<YieldInstruction> completionSource)
        {
            yield return instruction;
            completionSource.TrySetResult(instruction);
        }

        private static IEnumerator AwaitableRoutine(CustomYieldInstruction                       instruction,
                                                    TaskCompletionSource<CustomYieldInstruction> completionSource)
        {
            yield return instruction;
            completionSource.TrySetResult(instruction);
        }
    }

    public static class ReflectionExtensions
    {
        public static T GetField<T>(this object source, string name)
        {
            FieldInfo field = AccessTools.Field(source.GetType(), name);

            return field == null ? default(T) : (T)field.GetValue(source);
        }

        public static void SetField(this object source, string name, object value)
        {
            FieldInfo field = AccessTools.Field(source.GetType(), name);

            if (field != null)
                field.SetValue(source, value);
        }

        public static T GetProperty<T>(this object source, string name)
        {
            PropertyInfo property = AccessTools.Property(source.GetType(), name);

            return property == null ? default(T) : (T)property.GetValue(source, null);
        }

        public static void SetProperty(this object source, string name, object value)
        {
            PropertyInfo property = AccessTools.Property(source.GetType(), name);

            if (property != null)
                property.SetValue(source, value, null);
        }

        public static void InvokeMethod(this object source, string name, params object[] parameters)
        {
            MethodInfo method = AccessTools.Method(source.GetType(), name);

            if (method != null)
                method.Invoke(source, parameters);
        }

        public static void InvokeMethod(this   object   source, string name, Type[] methodParameters,
                                        params object[] invokeParameters)
        {
            MethodInfo method = AccessTools.Method(source.GetType(), name, methodParameters);

            if (method != null)
                method.Invoke(source, invokeParameters);
        }
    }

    public static class TransformExtensions
    {
        public static Transform FindChildRecursive(this Transform parent, string name)
        {
            if (parent == null)
                return null;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);

                if (child != null && child.name.Contains(name))
                    return child;

                Transform result = child.FindChildRecursive(name);

                if (result.IsObjectExistent())
                    return result;
            }

            return null;
        }
    }

    public static class GameModeExtensions
    {
        public static string GetName(this GameModeType gameMode) => GameModeUtility.GetGameModeName(gameMode);

        public static GorillaGameManager GetGameManager(this GameModeType gameMode) =>
                GameModeUtility.GetGameModeInstance(gameMode);
    }

    public static class PlayerExtensions
    {
        public static NetPlayer AsNetPlayer(this Player player)
        {
            if (player == null || NetworkSystem.Instance == null)
                return null;

            return NetworkSystem.Instance.GetPlayer(player.ActorNumber);
        }

        public static Player GetPlayer(this NetPlayer netPlayer)
        {
            PunNetPlayer punNetPlayer = netPlayer as PunNetPlayer;

            return punNetPlayer == null ? null : punNetPlayer.PlayerRef;
        }

        public static string GetName(this NetPlayer netPlayer, bool limitLength = true)
        {
            if (netPlayer == null)
                return string.Empty;

            string nickName    = netPlayer.NickName;
            string defaultName = netPlayer.DefaultName;
            string playerName  = string.IsNullOrWhiteSpace(nickName) ? defaultName : nickName;

            if (string.IsNullOrEmpty(playerName))
                playerName = string.Empty;

            return limitLength ? playerName.LimitLength(12) : playerName;
        }

        public static GetAccountInfoResult GetAccountInfo(this NetPlayer               netPlayer,
                                                          Action<GetAccountInfoResult> callback,
                                                          double                       maxCacheTime = double.MaxValue) =>
                PlayerUtility.GetAccountInfo(netPlayer.UserId, callback, maxCacheTime);

        public static DateTime GetAccountCreation(this NetPlayer netPlayer, Action<DateTime> callback,
                                                  double         maxCacheTime = double.MaxValue)
        {
            GetAccountInfoResult result = PlayerUtility.GetAccountInfo(netPlayer.UserId,
                    delegate(GetAccountInfoResult accountInfo)
                    {
                        DateTime created;

                        if (TryGetCreated(accountInfo, out created))
                            callback?.Invoke(created);
                    }, maxCacheTime);

            DateTime cachedCreated;

            return TryGetCreated(result, out cachedCreated) ? cachedCreated : DateTime.MinValue;
        }

        private static bool TryGetCreated(GetAccountInfoResult result, out DateTime created)
        {
            created = DateTime.MinValue;

            if (result == null || result.AccountInfo == null || result.AccountInfo.TitleInfo == null)
                return false;

            object value = result.AccountInfo.TitleInfo.Created;

            if (value is DateTime dateTime)
            {
                created = dateTime;

                return true;
            }

            return false;
        }

        public static bool GetTutorialCompletion(this NetPlayer player)
        {
            try
            {
                return NetworkSystem.Instance.GetPlayerTutorialCompletion(player.ActorNumber);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTagged(this NetPlayer player)
        {
            GorillaGameManager gameManager = GorillaGameManager.instance;

            return gameManager != null && player.IsTagged(gameManager);
        }

        public static bool IsTagged(this NetPlayer player, GorillaGameManager gameManager)
        {
            if (player == null || gameManager == null)
                return false;

            GorillaHuntManager huntManager = gameManager as GorillaHuntManager;

            if (huntManager != null)
                return PlayerUtility.IsTagged(player, huntManager);

            GorillaTagManager tagManager = gameManager as GorillaTagManager;

            if (tagManager != null)
                return tagManager.isCurrentlyTag
                               ? PlayerUtility.IsTagger(player, tagManager)
                               : PlayerUtility.IsTagged(player, tagManager);

            return false;
        }

        public static bool IsParticipant(this NetPlayer player) => PlayerUtility.IsParticipant(player);
    }

    public static class RigExtensions
    {
        public static float GetScaleFactor(this VRRig rig)
        {
            try
            {
                FieldInfo field = AccessTools.Field(typeof(VRRig), "lastScaleFactor");

                return field == null ? 1f : (float)field.GetValue(rig);
            }
            catch
            {
                return 1f;
            }
        }

        public static void GetCosmetics(this VRRig                           rig, out CosmeticsController.CosmeticSet currentWornSet,
                                        out  CosmeticsController.CosmeticSet tryOnSet)
        {
            currentWornSet = rig.cosmeticSet;
            tryOnSet       = rig.tryOnSet;
        }

        public static bool InTryOnRoom(this VRRig rig) => rig != null && rig.inTryOnRoom;

        public static CosmeticsController.CosmeticSet GetCosmetics(this VRRig rig)
        {
            CosmeticsController.CosmeticSet current;
            CosmeticsController.CosmeticSet tryOn;
            rig.GetCosmetics(out current, out tryOn);

            return rig.InTryOnRoom() ? tryOn : current;
        }

        public static GorillaIK GetGorilaIK(this VRRig rig)
        {
            if (rig == null)
                return null;

            try
            {
                FieldInfo field = AccessTools.Field(typeof(VRRig), "myIk");
                GorillaIK ik    = field == null ? null : field.GetValue(rig) as GorillaIK;

                return ik != null ? ik : rig.GetComponent<GorillaIK>();
            }
            catch
            {
                return rig.GetComponent<GorillaIK>();
            }
        }

        public static Transform GetBone(this VRRig rig, GorillaRigBone bone)
        {
            GorillaIK ik = rig.GetGorilaIK();

            if (ik == null)
                return null;

            switch (bone)
            {
                case GorillaRigBone.Head:
                    return ik.headBone != null ? ik.headBone : rig.headMesh.transform;

                case GorillaRigBone.Body:
                    return ik.bodyBone != null && ik.bodyBone.Find("body") != null
                                   ? ik.bodyBone.Find("body")
                                   : ik.bodyBone;

                case GorillaRigBone.LeftUpperArm:
                    return ik.leftUpperArm != null ? ik.leftUpperArm : ik.bodyBone.Find("shoulder.L");

                case GorillaRigBone.RightUpperArm:
                    return ik.rightUpperArm != null ? ik.rightUpperArm : ik.bodyBone.Find("shoulder.R");

                case GorillaRigBone.LeftLowerArm:
                    return ik.leftLowerArm != null ? ik.leftLowerArm : ik.bodyBone.Find("shoulder.L/forearm.L");

                case GorillaRigBone.RightLowerArm:
                    return ik.rightLowerArm != null ? ik.rightLowerArm : ik.bodyBone.Find("shoulder.R/forearm.R");

                case GorillaRigBone.LeftHand:
                    return ik.leftHand;

                case GorillaRigBone.RightHand:
                    return ik.rightHand;
            }

            return null;
        }
    }
}

namespace GorillaLibrary.Utilities
{
    public static class GameModeUtility
    {
        private static readonly List<GameModeWrapper> GameModes = new();

        public static GameModeWrapper CurrentGameMode { get; private set; }

        public static void RegisterGameMode(GameModeWrapper gameMode)
        {
            if (gameMode == null || string.IsNullOrEmpty(gameMode.ID))
                return;

            if (GameModes.Any(existing => existing.ID == gameMode.ID))
                return;

            GameModes.Add(gameMode);
        }

        public static void SetCurrentGameMode(string gamemode)
        {
            if (string.IsNullOrEmpty(gamemode))
            {
                CurrentGameMode = null;

                return;
            }

            CurrentGameMode = FindGameModeInString(gamemode) ?? new GameModeWrapper(gamemode, gamemode);
        }

        public static GameModeWrapper FindGameModeInString(string gmString)
        {
            if (string.IsNullOrEmpty(gmString))
                return null;

            GameModeWrapper found =
                    GameModes.LastOrDefault(gamemode => !string.IsNullOrEmpty(gamemode.ID) &&
                                                        gmString.Contains(gamemode.ID));

            return found ?? new GameModeWrapper(gmString, gmString);
        }

        public static GameModeWrapper FindGameModeFromId(string id) => GetGameMode(gamemode => gamemode.ID == id) ?? new GameModeWrapper(id, id);

        public static GameModeWrapper GetGameMode(Func<GameModeWrapper, bool> predicate) =>
                GameModes.LastOrDefault(predicate);

        public static string GetGameModeName(GameModeType gameModeType)
        {
            try
            {
                GorillaGameManager gameManager = GetGameModeInstance(gameModeType);

                if (gameManager != null)
                    return gameManager.GameModeName();
            }
            catch { }

            string name = gameModeType.ToString();

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        }

        public static GorillaGameManager GetGameModeInstance(GameModeType gameModeType)
        {
            try
            {
                Object             gameMode = GameMode.GetGameModeInstance(gameModeType);
                GorillaGameManager manager  = gameMode as GorillaGameManager;

                return manager != null && manager.IsObjectExistent() ? manager : null;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsSuperGameMode(this GameModeType gameMode) =>
                Enum.IsDefined(typeof(GameModeType), (int)gameMode) &&
                gameMode.ToString().ToLowerInvariant().StartsWith("super");

        public static IEnumerable<NetPlayer> GetTaggedPlayers(GorillaGameManager gameManager)
        {
            if (NetworkSystem.Instance == null || !NetworkSystem.Instance.InRoom)
                return Enumerable.Empty<NetPlayer>();

            return NetworkSystem.Instance.AllNetPlayers.Where(player => player.IsTagged(gameManager));
        }

        public static IEnumerable<NetPlayer> GetParticipants()
        {
            if (NetworkSystem.Instance == null || !NetworkSystem.Instance.InRoom)
                return Enumerable.Empty<NetPlayer>();

            return NetworkSystem.Instance.AllNetPlayers.Where(player => player.IsParticipant());
        }
    }

    public static class PlayerUtility
    {
        private static readonly Dictionary<string, CachedAccountInfo> AccountInfoCache = new();

        public static GetAccountInfoResult GetAccountInfo(string                       userId,
                                                          Action<GetAccountInfoResult> onAccountInfoRecieved,
                                                          double                       maxCacheTime = double.MaxValue)
        {
            if (string.IsNullOrEmpty(userId))
                return null;

            CachedAccountInfo cached;

            if (AccountInfoCache.TryGetValue(userId, out cached) &&
                (DateTime.Now - cached.CacheTime).TotalMinutes < maxCacheTime)
                return cached.AccountInfo;

            if (!PlayFabClientAPI.IsClientLoggedIn())
                return null;

            Action<GetAccountInfoResult> success = delegate(GetAccountInfoResult accountInfo)
                                                   {
                                                       AccountInfoCache[userId] =
                                                               new CachedAccountInfo(accountInfo, DateTime.Now);

                                                       onAccountInfoRecieved?.Invoke(accountInfo);
                                                   };

            Action<PlayFabError> failure = delegate(PlayFabError error)
                                           {
                                               if (error != null)
                                                   GorillaLibraryCompat.LogError(error.GenerateErrorReport());
                                           };

            PlayFabClientAPI.GetAccountInfo(new GetAccountInfoRequest
            {
                    PlayFabId = userId,
            }, success, failure);

            return null;
        }

        public static Task<GetAccountInfoResult> GetAccountInfo(string userId, double maxCacheTime = double.MaxValue)
        {
            TaskCompletionSource<GetAccountInfoResult> completionSource = new();

            GetAccountInfoResult result = GetAccountInfo(userId, newResult =>
                                                                 {
                                                                     if (!completionSource.Task.IsCompleted)
                                                                         completionSource.SetResult(newResult);
                                                                 }, maxCacheTime);

            if (result != null)
                completionSource.SetResult(result);

            return completionSource.Task;
        }

        public static bool IsTagged(NetPlayer player, GorillaTagManager tagManager) =>
                tagManager != null && tagManager.currentInfected != null && tagManager.currentInfected.Contains(player);

        public static bool IsTagger(NetPlayer player, GorillaTagManager tagManager) =>
                tagManager != null && tagManager.currentIt == player;

        public static bool IsTagged(NetPlayer player, GorillaHuntManager huntManager) =>
                huntManager != null && huntManager.currentHunted != null && huntManager.currentHunted.Contains(player);

        public static bool IsParticipant(NetPlayer player)
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(GameMode), "CanParticipate");

                return method != null && (bool)method.Invoke(null, new object[] { player, });
            }
            catch
            {
                return false;
            }
        }

        private sealed class CachedAccountInfo
        {
            public readonly GetAccountInfoResult AccountInfo;
            public readonly DateTime             CacheTime;

            public CachedAccountInfo(GetAccountInfoResult accountInfo, DateTime cacheTime)
            {
                AccountInfo = accountInfo;
                CacheTime   = cacheTime;
            }
        }
    }

    public static class RigUtility
    {
        public enum NetInputType
        {
            FaceButtonTouch,
            FaceButtonPress,
            Grip,
            Trigger,
        }

        public static VRRig LocalRig
        {
            get
            {
                if (VRRig.LocalRig != null)
                    return VRRig.LocalRig;

                return GorillaTagger.Instance != null ? GorillaTagger.Instance.offlineVRRig : null;
            }
        }

        public static bool Initialized => VRRigCache.isInitialized;

        public static Dictionary<NetPlayer, RigContainer> Rigs => VRRigCache.rigsInUse;

        public static NetInputTracker<float> LeftGrip             { get; private set; }
        public static NetInputTracker<float> RightGrip            { get; private set; }
        public static NetInputTracker<float> LeftTrigger          { get; private set; }
        public static NetInputTracker<float> RightTrigger         { get; private set; }
        public static NetInputTracker<bool>  LeftFaceButton       { get; private set; }
        public static NetInputTracker<bool>  RightFaceButton      { get; private set; }
        public static NetInputTracker<bool>  LeftFaceButtonTouch  { get; private set; }
        public static NetInputTracker<bool>  RightFaceButtonTouch { get; private set; }

        public static void Initialize()
        {
            LeftGrip             = new NetInputTracker<float>(NetInputType.Grip,    true);
            RightGrip            = new NetInputTracker<float>(NetInputType.Grip,    false);
            LeftTrigger          = new NetInputTracker<float>(NetInputType.Trigger, true);
            RightTrigger         = new NetInputTracker<float>(NetInputType.Trigger, false);
            LeftFaceButton       = new NetInputTracker<bool>(NetInputType.FaceButtonPress, true);
            RightFaceButton      = new NetInputTracker<bool>(NetInputType.FaceButtonPress, false);
            LeftFaceButtonTouch  = new NetInputTracker<bool>(NetInputType.FaceButtonTouch, true);
            RightFaceButtonTouch = new NetInputTracker<bool>(NetInputType.FaceButtonTouch, false);
        }

        public static bool TryGetRig(NetPlayer player, out RigContainer rig)
        {
            rig = null;

            try
            {
                if (VRRigCache.Instance == null)
                    return false;

                object[] parameters = new object[] { player, null, };
                MethodInfo method = AccessTools.Method(typeof(VRRigCache), "TryGetVrrig",
                        new[] { typeof(NetPlayer), typeof(RigContainer).MakeByRefType(), });

                if (method == null)
                    return false;

                bool result = (bool)method.Invoke(VRRigCache.Instance, parameters);
                rig = parameters[1] as RigContainer;

                return result;
            }
            catch
            {
                return false;
            }
        }

        public static RigContainer GetRig(NetPlayer player)
        {
            RigContainer playerRig;

            return TryGetRig(player, out playerRig) ? playerRig : null;
        }

        public class NetInputTracker<T>
        {
            private readonly NetInputType inputType;
            private readonly bool         useLeftHand;

            public NetInputTracker(NetInputType type, bool leftHand)
            {
                if (typeof(T) != typeof(bool) && typeof(T) != typeof(float))
                    throw new ArgumentException("NetInputTracker only accepts bool and float.");

                if (typeof(T) == typeof(float) && type != NetInputType.Grip && type != NetInputType.Trigger)
                    throw new ArgumentException("Only trigger and grip inputs can be floats.");

                inputType   = type;
                useLeftHand = leftHand;
            }

            public T GetValue(VRRig rig)
            {
                object returnValue = null;

                switch (inputType)
                {
                    case NetInputType.FaceButtonTouch:
                    case NetInputType.FaceButtonPress:
                        returnValue = (useLeftHand ? rig.leftThumb : rig.rightThumb).calcT > 0.5f;

                        return (T)returnValue;

                    case NetInputType.Grip:
                        if (typeof(T) == typeof(bool))
                            returnValue = (useLeftHand ? rig.leftMiddle : rig.rightMiddle).calcT > 0.5f;
                        else
                            returnValue = (useLeftHand ? rig.leftMiddle : rig.rightMiddle).calcT;

                        return (T)returnValue;

                    case NetInputType.Trigger:
                        if (typeof(T) == typeof(bool))
                            returnValue = (useLeftHand ? rig.leftIndex : rig.rightIndex).calcT > 0.5f;
                        else
                            returnValue = (useLeftHand ? rig.leftIndex : rig.rightIndex).calcT;

                        return (T)returnValue;
                }

                throw new Exception("Invalid input type " + inputType + ".");
            }
        }
    }

    public static class InputUtility
    {
        private static List<InputTracker>       inputTrackers;
        private static ControllerInputPollerExt pollerExt;
        public static  InputDevice              LeftController  { get; private set; }
        public static  InputDevice              RightController { get; private set; }
        public static  InputTracker<float>      LeftGrip        { get; private set; }
        public static  InputTracker<float>      RightGrip       { get; private set; }
        public static  InputTracker<float>      LeftTrigger     { get; private set; }
        public static  InputTracker<float>      RightTrigger    { get; private set; }
        public static  InputTracker<bool>       LeftStickClick  { get; private set; }
        public static  InputTracker<bool>       RightStickClick { get; private set; }
        public static  InputTracker<bool>       LeftPrimary     { get; private set; }
        public static  InputTracker<bool>       RightPrimary    { get; private set; }
        public static  InputTracker<bool>       LeftSecondary   { get; private set; }
        public static  InputTracker<bool>       RightSecondary  { get; private set; }
        public static  InputTracker<Vector2>    LeftStickAxis   { get; private set; }
        public static  InputTracker<Vector2>    RightStickAxis  { get; private set; }

        public static void Initialize()
        {
            LeftController  = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            RightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            Traverse poller = Traverse.Create(ControllerInputPoller.instance);
            pollerExt = new ControllerInputPollerExt();
            Traverse pollerExtTraverse = Traverse.Create(pollerExt);

            LeftGrip       = new InputTracker<float>(poller.Field("leftControllerGripFloat"),   XRNode.LeftHand);
            RightGrip      = new InputTracker<float>(poller.Field("rightControllerGripFloat"),  XRNode.RightHand);
            LeftTrigger    = new InputTracker<float>(poller.Field("leftControllerIndexFloat"),  XRNode.LeftHand);
            RightTrigger   = new InputTracker<float>(poller.Field("rightControllerIndexFloat"), XRNode.RightHand);
            LeftPrimary    = new InputTracker<bool>(poller.Field("leftControllerPrimaryButton"),    XRNode.LeftHand);
            RightPrimary   = new InputTracker<bool>(poller.Field("rightControllerPrimaryButton"),   XRNode.RightHand);
            LeftSecondary  = new InputTracker<bool>(poller.Field("leftControllerSecondaryButton"),  XRNode.LeftHand);
            RightSecondary = new InputTracker<bool>(poller.Field("rightControllerSecondaryButton"), XRNode.RightHand);
            LeftStickClick =
                    new InputTracker<bool>(pollerExtTraverse.Field("leftControllerStickButton"), XRNode.LeftHand);

            RightStickClick =
                    new InputTracker<bool>(pollerExtTraverse.Field("rightControllerStickButton"), XRNode.RightHand);

            LeftStickAxis =
                    new InputTracker<Vector2>(pollerExtTraverse.Field("leftControllerStickAxis"), XRNode.LeftHand);

            RightStickAxis =
                    new InputTracker<Vector2>(pollerExtTraverse.Field("rightControllerStickAxis"), XRNode.RightHand);

            inputTrackers = new List<InputTracker>
            {
                    LeftGrip,
                    RightGrip,
                    LeftTrigger,
                    RightTrigger,
                    LeftPrimary,
                    RightPrimary,
                    LeftSecondary,
                    RightSecondary,
                    LeftStickClick,
                    RightStickClick,
                    LeftStickAxis,
                    RightStickAxis,
            };
        }

        public static void Update()
        {
            if (pollerExt == null || inputTrackers == null)
                Initialize();

            pollerExt.Update();

            foreach (InputTracker input in inputTrackers)
                input.UpdateValues();
        }

        private class ControllerInputPollerExt
        {
            public Vector2 leftControllerStickAxis;
            public bool    leftControllerStickButton;
            public Vector2 rightControllerStickAxis;
            public bool    rightControllerStickButton;

            public void Update()
            {
                LeftController.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out leftControllerStickButton);
                RightController.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out rightControllerStickButton);
                LeftController.TryGetFeatureValue(CommonUsages.primary2DAxis, out leftControllerStickAxis);
                RightController.TryGetFeatureValue(CommonUsages.primary2DAxis, out rightControllerStickAxis);
            }
        }
    }

    public static class ServerUtility
    {
        public static string GetRegionCode()
        {
            try
            {
                return (PhotonNetwork.CloudRegion ?? NetworkSystem.Instance.CurrentRegion).Replace("/*", "");
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string GetRegionName() => GetRegionNameFromCode(GetRegionCode());

        public static string GetRegionNameFromCode(string regionCode)
        {
            switch (regionCode)
            {
                case "us":   return "United States (East)";
                case "usw":  return "United States (West)";
                case "eu":   return "Europe";
                case "asia": return "Asia";
                case "au":   return "Australia";
                case "cae":  return "Canada";
                case "hk":   return "Hong Kong";
                case "in":   return "India";
                case "jp":   return "Japan";
                case "za":   return "South Africa";
                case "sa":   return "South America";
                case "kr":   return "South Korea";
                case "tr":   return "Turkey";
                case "uae":  return "United Arab Emirates";
                case "ussc": return "United States (South Central)";
                default:     return regionCode;
            }
        }
    }

    public static class XRUtility
    {
        private static bool checkedSubsystem;
        private static bool isSubsystemActive;

        public static bool IsXRSubsystemActive
        {
            get
            {
                if (!checkedSubsystem)
                {
                    List<ISubsystem> subsystems = new();
                    SubsystemManager.GetSubsystems(subsystems);
                    isSubsystemActive = subsystems.Count > 0 && subsystems.Exists(subsystem => subsystem.running);

                    checkedSubsystem = true;
                }

                return isSubsystemActive;
            }
        }
    }

    public static class ZoneUtility
    {
        public static ZoneManagement ZoneManagement
        {
            get
            {
                if (ZoneManagement.instance == null)
                    ZoneManagement.FindInstance();

                return ZoneManagement.instance;
            }
        }

        public static GameObject[] Objects
        {
            get
            {
                try
                {
                    ZoneManagement instance = ZoneManagement;

                    FieldInfo reflectedField = typeof(ZoneManagement).GetField(
                            "allObjects",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (reflectedField == null)
                        return null;

                    return reflectedField.GetValue(instance) as GameObject[];
                }
                catch
                {
                    return null;
                }
            }
        }

        public static ZoneData[] Zones
        {
            get
            {
                try
                {
                    ZoneManagement instance = ZoneManagement;

                    FieldInfo reflectedField = typeof(ZoneManagement).GetField(
                            "zones",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (reflectedField == null)
                        return null;

                    return reflectedField.GetValue(instance) as ZoneData[];
                }
                catch
                {
                    return null;
                }
            }
        }

        public static ZoneData GetZoneData(GTZone zone)
        {
            try
            {
                ZoneManagement instance = ZoneManagement;
                MethodInfo method = typeof(ZoneManagement).GetMethod("GetZoneData",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                return method == null ? null : method.Invoke(instance, [zone,]) as ZoneData;
            }
            catch
            {
                return null;
            }
        }
    }

    public static class AssetBundleUtility
    {
        public static AssetBundle LoadBundle(Assembly assembly, string path)
        {
            Stream stream = assembly.GetManifestResourceStream(path);

            if (stream == null)
                return null;

            AssetBundle bundle = AssetBundle.LoadFromStream(stream);
            stream.Close();

            return bundle;
        }

        async public static Task<AssetBundle> LoadBundleAsync(Assembly assembly, string path)
        {
            Stream stream = assembly.GetManifestResourceStream(path);

            if (stream == null)
                return null;

            AssetBundleCreateRequest request = AssetBundle.LoadFromStreamAsync(stream);
            await request.AsAwaitable();
            stream.Close();

            return request.assetBundle;
        }

        async public static Task<T> LoadAssetAsync<T>(AssetBundle bundle, string name) where T : Object
        {
            if (bundle == null)
                return null;

            AssetBundleRequest request = bundle.LoadAssetAsync<T>(name);
            await request.AsAwaitable();

            return request.asset as T;
        }

        async public static Task<T[]> LoadAssetsWithSubAssetsAsync<T>(AssetBundle bundle, string name) where T : Object
        {
            if (bundle == null)
                return new T[0];

            AssetBundleRequest request = bundle.LoadAssetWithSubAssetsAsync<T>(name);
            await request.AsAwaitable();

            return request.allAssets.Cast<T>().ToArray();
        }
    }

    public static class TextureLoaderUtility
    {
        private static readonly Dictionary<string, TaskCompletionSource<Texture2D>> Cache = new();

        async public static Task<Texture2D> LoadTexture(string url, TextureFormat format = TextureFormat.RGB24)
        {
            TaskCompletionSource<Texture2D> completionSource;

            if (Cache.TryGetValue(url, out completionSource))
                return completionSource.Task.IsCompleted ? completionSource.Task.Result : await completionSource.Task;

            completionSource = new TaskCompletionSource<Texture2D>();
            Cache[url]       = completionSource;

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                    await Task.Yield();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Texture2D texture = new(2, 2, format, false);
                    texture.LoadImage(request.downloadHandler.data);
                    completionSource.TrySetResult(texture);

                    return texture;
                }
            }

            completionSource.TrySetResult(null);

            return null;
        }
    }

    public static class WebRequestUtility
    {
        async public static void SendRequest(string            url, WebRequest model, Action<string> onSuccess,
                                             Action<Exception> onError)
        {
            UnityWebRequest request = null;

            try
            {
                string payloadString =
                        model.PostData != null ? JsonConvert.SerializeObject(model.PostData) : string.Empty;

                switch (model.Method)
                {
                    case RequestMethod.Get:
                        request = UnityWebRequest.Get(url);

                        break;

                    case RequestMethod.Post:
                        request                 = new UnityWebRequest(url, "POST");
                        request.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payloadString));
                        request.downloadHandler = new DownloadHandlerBuffer();

                        break;

                    case RequestMethod.Put:
                        request                 = new UnityWebRequest(url, "PUT");
                        request.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payloadString));
                        request.downloadHandler = new DownloadHandlerBuffer();

                        break;

                    case RequestMethod.Delete:
                        request = UnityWebRequest.Delete(url);

                        break;

                    default:
                        request                 = new UnityWebRequest(url, model.Method.ToString().ToUpperInvariant());
                        request.downloadHandler = new DownloadHandlerBuffer();

                        break;
                }

                request.SetRequestHeader("Content-Type",
                        string.IsNullOrEmpty(model.ContentType) ? "application/json" : model.ContentType);

                if (model.Headers != null)
                    foreach (KeyValuePair<string, string> header in model.Headers)
                        request.SetRequestHeader(header.Key, header.Value);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                    await Task.Yield();

                if (request.result == UnityWebRequest.Result.Success)
                    onSuccess?.Invoke(request.downloadHandler.text);
                else
                    onError?.Invoke(new Exception(request.error));
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                request?.Dispose();
            }
        }
    }

    public static class WebSocketUtility
    {
        public static Action<WebSocketData>            OnConnected;
        public static Action<WebSocketData>            OnDisconnect;
        public static Action<WebSocketData, Exception> OnError;

        private static readonly Dictionary<string, WebSocketData> Data = new();

        async public static Task<WebSocketData> Connect(string url, CancellationToken cancellationToken = default)
        {
            WebSocketData existing;

            if (Data.TryGetValue(url, out existing) && existing.IsConnected)
                return existing;

            ClientWebSocket         socket = new();
            CancellationTokenSource cts    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            WebSocketData           data   = new(socket, cts);
            Data[url] = data;

            try
            {
                await socket.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
                OnConnected?.Invoke(data);
                _ = ReceiveLoop(data);

                return data;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(data, ex);

                return data;
            }
        }

        private async static Task ReceiveLoop(WebSocketData data)
        {
            byte[] buffer = new byte[8192];

            while (data != null && data.IsConnected && !data.CancellationSource.IsCancellationRequested)
                try
                {
                    WebSocketReceiveResult result = await data.Socket
                                                              .ReceiveAsync(new ArraySegment<byte>(buffer),
                                                                       data.CancellationSource.Token)
                                                              .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await Disconnect(data).ConfigureAwait(false);

                        return;
                    }

                    string  json    = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    JObject message = JObject.Parse(json);
                    string  type    = message["type"] != null ? message["type"].ToString() : null;

                    if (string.IsNullOrEmpty(type))
                        continue;

                    List<Action<JObject>> handlers;

                    if (data.Subscribers.TryGetValue(type, out handlers))
                        foreach (Action<JObject> handler in handlers.ToArray())
                            handler?.Invoke(message);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(data, ex);

                    return;
                }
        }

        async public static Task SendMessage(WebSocketData     data, object payload,
                                             CancellationToken cancellationToken = default)
        {
            if (data == null || !data.IsConnected)
                return;

            JObject message = new();
            message["payload"] = payload == null ? JValue.CreateNull() : JToken.FromObject(payload);

            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
            await data.Socket
                      .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                      .ConfigureAwait(false);
        }

        public static void Subscribe(WebSocketData data, string messageType, Action<JObject> handler)
        {
            if (data == null || string.IsNullOrEmpty(messageType) || handler == null)
                return;

            List<Action<JObject>> list;

            if (!data.Subscribers.TryGetValue(messageType, out list))
            {
                list                          = new List<Action<JObject>>();
                data.Subscribers[messageType] = list;
            }

            list.Add(handler);
        }

        public static void Unsubscribe(WebSocketData data, string messageType, Action<JObject> handler)
        {
            if (data == null || string.IsNullOrEmpty(messageType) || handler == null)
                return;

            List<Action<JObject>> list;

            if (data.Subscribers.TryGetValue(messageType, out list))
                list.RemoveAll(existing => existing == handler);
        }

        public static void ClearSubscribers(WebSocketData data)
        {
            if (data != null)
                data.Subscribers.Clear();
        }

        async public static Task Disconnect(WebSocketData data)
        {
            if (data == null)
                return;

            try
            {
                if (data.Socket != null && (data.IsConnected || data.Socket.State == WebSocketState.CloseReceived))
                    await data.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                              .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(data, ex);
            }
            finally
            {
                data.CancellationSource?.Cancel();
                data.Socket?.Dispose();
                OnDisconnect?.Invoke(data);
            }
        }
    }
}