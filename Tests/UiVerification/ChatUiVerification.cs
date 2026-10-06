using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

// 검증 시 Assets/Editor로 복사해 실행하고 빌드 후 임시 복사본을 제거한다.
public sealed class ChatUiVerification : EditorWindow
{
    private static ChatUiVerification _window;
    private readonly ConcurrentQueue<byte[]> _received = new();
    private GameObject _host;
    private WorldManager _world;
    private TestClientController _controller;
    private TcpSession _session;
    private TcpListener _listener;
    private TcpClient _peer;
    private int _phase;
    private double _deadline;
    private bool _finished;
    private Event _pendingKey;

    public static void Run()
    {
        try
        {
            _window = CreateInstance<ChatUiVerification>();
            _window.titleContent = new GUIContent("Chat UI verification");
            _window.position = new Rect(100, 100, 960, 700);
            _window.Setup();
            _window.Show();
            _window.Focus();
            _window._deadline = EditorApplication.timeSinceStartup + 40;
            EditorApplication.update += _window.Tick;
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Setup()
    {
        _host = new GameObject("UI verification fixture");
        _host.SetActive(false);
        NetworkManager network = _host.AddComponent<NetworkManager>();
        _world = _host.AddComponent<WorldManager>();
        PlatformMovementController movement = _host.AddComponent<PlatformMovementController>();
        _controller = _host.AddComponent<TestClientController>();
        Set(_world, "networkManager", network);
        Set(movement, "_network", network); Set(movement, "_world", _world);
        Set(_controller, "_networkManager", network); Set(_controller, "_worldManager", _world); Set(_controller, "_platformMovement", movement);
        EnterGameData entry = new() { Result = EnterGameResult.Success, CharacterId = 1001, Name = "local" };
        Invoke(_world, "OnEnterGame", entry);
        Invoke(_world, "OnPlayerEntered", new PlayerEnterData { CharacterId = 2001, Name = "remote" });
        Invoke(_controller, "OnEnterGameReceived", entry);
        VerifyRendering();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _session = new TcpSession();
        Task.Run(() => _session.ConnectAsync("127.0.0.1", port)).GetAwaiter().GetResult();
        _peer = _listener.AcceptTcpClient();
        _ = Task.Run(() =>
        {
            try
            {
                using BinaryReader reader = new(_peer.GetStream());
                while (true)
                {
                    ushort length = reader.ReadUInt16();
                    ushort opcode = reader.ReadUInt16();
                    byte[] body = reader.ReadBytes(length - 4);
                    if (opcode != (ushort)GamePacketOpcode.ChatRequest || body.Length != 128)
                        throw new InvalidDataException("Unexpected UI chat packet");
                    _received.Enqueue(body);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        Set(network, "_gameSession", _session);
        Set(_controller, "_chatInput", "first");
        Set(_controller, "_refocusChat", true);
    }

    private void VerifyRendering()
    {
        PlayerView local = _world.LocalPlayer;
        PlayerView remote = _world.RemotePlayers.Single();
        Check(local.GetComponent<SortingGroup>().sortingOrder > remote.GetComponent<SortingGroup>().sortingOrder, "Local sorting group did not precede remote");
        foreach (GameObject player in new[] { local.gameObject, remote.gameObject }) player.layer = 31;
        RenderPipelineAsset previousDefault = GraphicsSettings.defaultRenderPipeline;
        RenderPipelineAsset previousQuality = QualitySettings.renderPipeline;
        GameObject cameraObject = new("Render verification");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.orthographicSize = 1;
        camera.transform.position = new Vector3(0, 0, -10);
        camera.cullingMask = 1 << 31;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        RenderTexture target = new(32, 32, 16);
        Texture2D pixels = new(32, 32);
        RenderTexture previousTarget = RenderTexture.active;
        try
        {
            GraphicsSettings.defaultRenderPipeline = null;
            QualitySettings.renderPipeline = null;
            camera.targetTexture = target;
            foreach (PlayerView view in new[] { local, remote })
                view.GetComponent<SpriteRenderer>().sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            Color overlap = Sample();
            remote.GetComponent<SpriteRenderer>().enabled = false;
            Color localOnly = Sample();
            remote.GetComponent<SpriteRenderer>().enabled = true;
            local.GetComponent<SpriteRenderer>().enabled = false;
            Color remoteOnly = Sample();
            local.GetComponent<SpriteRenderer>().enabled = true;
            Check(ColorDistance(overlap, localOnly) < 0.03f && ColorDistance(overlap, remoteOnly) > 0.1f, "Overlapping pixel did not render local player on top");
            Pass("overlapping players render the local sprite in front");
        }
        finally
        {
            RenderTexture.active = previousTarget;
            GraphicsSettings.defaultRenderPipeline = previousDefault;
            QualitySettings.renderPipeline = previousQuality;
            DestroyImmediate(cameraObject); DestroyImmediate(target); DestroyImmediate(pixels);
        }
        Color Sample()
        {
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
            pixels.Apply();
            return pixels.GetPixel(16, 16);
        }

        GameObject template = new("Player prefab verification");
        template.SetActive(false);
        PlayerView prefab = template.AddComponent<PlayerView>();
        template.AddComponent<SpriteRenderer>();
        GameObject child = new("child sprite"); child.transform.SetParent(template.transform);
        child.AddComponent<SpriteRenderer>().sortingOrder = 100;
        Set(_world, "playerPrefab", prefab);
        PlayerView localPrefab = (PlayerView)Invoke(_world, "CreatePlayer", 4001u, true);
        PlayerView remotePrefab = (PlayerView)Invoke(_world, "CreatePlayer", 5001u, false);
        Check(localPrefab.GetComponentsInChildren<SpriteRenderer>(true).Length == 2 && localPrefab.GetComponent<SortingGroup>().sortingOrder > remotePrefab.GetComponent<SortingGroup>().sortingOrder, "Prefab child sorting did not use local group");
        Pass("prefab child sprites keep the local sorting priority");
        DestroyImmediate(localPrefab.gameObject); DestroyImmediate(remotePrefab.gameObject); DestroyImmediate(template);
        Set(_world, "playerPrefab", null);
    }

    private void OnGUI()
    {
        if (_controller == null || _finished) return;
        Event original = Event.current;
        try
        {
            if (Event.current.type == EventType.Repaint && _pendingKey != null)
            {
                Event.current = _pendingKey;
                _pendingKey = null;
            }
            EventType type = Event.current.type;
            KeyCode key = Event.current.keyCode;
            Invoke(_controller, "OnGUI");
            if (type == EventType.Repaint && _phase == 0)
            {
                Check(GUI.GetNameOfFocusedControl() == "MapChatInput", "Initial chat focus missing");
                _phase = 1; SendKey(KeyCode.Return);
            }
            else if (type == EventType.KeyDown && key == KeyCode.Return && _phase == 1)
            {
                Check((string)Get(_controller, "_chatInput") == "", "Enter did not clear submitted text");
                _phase = 2;
            }
            else if (type == EventType.Repaint && _phase == 2)
            {
                Check(GUI.GetNameOfFocusedControl() == "MapChatInput" && GUIUtility.keyboardControl != 0, "Enter removed chat focus");
                Pass("Enter sends chat and keeps the actual IMGUI textbox focused");
                _phase = 3; SendKey(KeyCode.N, 'n');
            }
            else if (type == EventType.KeyDown && key == KeyCode.N && _phase == 3)
            {
                Check((string)Get(_controller, "_chatInput") == "n", "Next keystroke did not enter chat");
                _phase = 4; SendKey(KeyCode.KeypadEnter);
            }
            else if (type == EventType.KeyDown && key == KeyCode.KeypadEnter && _phase == 4)
                _phase = 5;
            else if (type == EventType.Repaint && _phase == 5)
            {
                Check(GUI.GetNameOfFocusedControl() == "MapChatInput" && (string)Get(_controller, "_chatInput") == "", "Second chat lost focus");
                Pass("next keystroke and keypad Enter send a second message without clicking");
                _phase = 6; SendKey(KeyCode.Escape);
            }
            else if (type == EventType.KeyDown && key == KeyCode.Escape && _phase == 6)
            {
                Check(GUIUtility.keyboardControl == 0, "Esc did not release movement focus");
                Pass("Escape releases chat focus for movement");
                _phase = 7;
            }
        }
        catch (Exception exception) { Fail(exception); }
        finally { Event.current = original; }
    }

    private void Tick()
    {
        if (_finished) return;
        if (EditorApplication.timeSinceStartup > _deadline) { Fail(new TimeoutException($"UI phase {_phase}")); return; }
        Repaint();
        if (_phase != 7 || _received.Count != 2) return;
        try
        {
            byte[][] messages = _received.ToArray();
            Check(new PacketReader(messages[0], 128).ReadFixedString(128) == "first" && new PacketReader(messages[1], 128).ReadFixedString(128) == "n", "UI did not send the two real TCP messages in order");
            Pass("two chat messages arrive over TCP in order");
            Invoke(_controller, "OnPlayerChatReceived", new PlayerChatData { CharacterId = 1001, Message = "map" });
            Invoke(_controller, "OnWhisperReceived", new WhisperData { SenderCharacterId = 1001, TargetCharacterId = 2001, TargetName = "remote", Message = "whisper" });
            Invoke(_controller, "OnMapChanged", new ChangeMapData { Result = ChangeMapResult.Success, MapId = 100000001 });
            Check(((IList)Get(_controller, "_chatMessages")).Count == 1, "Map transition did not retain only whisper history");
            Set(_controller, "_chatInput", "pending");
            Invoke(_controller, "OnGameDisconnected");
            Check(((IList)Get(_controller, "_chatMessages")).Count == 0 && (string)Get(_controller, "_chatInput") == "", "Disconnect retained chat state");
            Pass("map changes preserve whisper history and disconnect clears chat state");
            _finished = true;
            EditorApplication.update -= Tick;
            Cleanup();
            EditorApplication.delayCall += Build;
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void SendKey(KeyCode code, char character = '\0') => EditorApplication.delayCall += () =>
    {
        // Editor 단축키 계층을 거치지 않고 활성 OnGUI 컨텍스트에 입력 이벤트를 전달한다.
        _pendingKey = new Event { type = EventType.KeyDown, keyCode = code, character = character };
        Repaint();
    };

    private void Cleanup()
    {
        _session?.Dispose(); _peer?.Dispose(); _listener?.Stop();
        if (_world != null)
        {
            foreach (PlayerView view in _world.RemotePlayers.ToArray()) DestroyImmediate(view.gameObject);
            if (_world.LocalPlayer != null) DestroyImmediate(_world.LocalPlayer.gameObject);
        }
        if (_host != null) DestroyImmediate(_host);
        Close();
    }

    private static void Build()
    {
        try
        {
            string output = Environment.GetCommandLineArgs().First(arg => arg.StartsWith("--ui-build=")).Substring("--ui-build=".Length);
            BuildReport report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(), output, BuildTarget.StandaloneWindows64, BuildOptions.None);
            Check(report.summary.result == BuildResult.Succeeded, "Windows build failed");
            Pass("Windows client build succeeds");
            EditorApplication.Exit(0);
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static object Invoke(object target, string name, params object[] values) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, values);
    private static float ColorDistance(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string message) => Debug.Log("[PASS] " + message);
    private static void Fail(Exception exception)
    {
        Debug.LogError("[FAIL] " + exception);
        if (_window != null) { _window._finished = true; EditorApplication.update -= _window.Tick; _window.Cleanup(); }
        EditorApplication.Exit(1);
    }
}
