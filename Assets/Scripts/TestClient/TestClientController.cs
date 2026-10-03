using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class TestClientController : MonoBehaviour
{
    private NetworkManager _networkManager;
    private WorldManager _worldManager;

    private string _moveX = "120";
    private string _moveY = "45";
    private string _mapId = "100000001";
    private string _status = "";
    private string _lastPlayerMove = "Last PlayerMove: none";
    private string _chatInput = "";
    private GUIStyle _playerLabelStyle;
    private GUIStyle _chatMessageStyle;
    private readonly List<string> _chatMessages = new();
    private readonly Dictionary<uint, string> _playerNames = new();
    private Vector2 _chatScroll;

    private const float ArrowMoveSpeed = 80f;
    private const float MoveSendInterval = 0.05f;

    private uint _currentMapId;
    private bool _inGame;
    private bool _sendingMove;
    private bool _sendingChat;
    private bool _changingMap;
    private int _moveVersion;
    private float _nextMoveSendTime;
    private Vector2 _moveTarget;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateTestClient()
    {
        if (FindFirstObjectByType<TestClientController>() != null)
            return;

        // 샘플 씬에서도 별도 프리팹 연결 없이 네트워크 테스트를 시작할 수 있게 구성한다.
        GameObject gameObject = new("Test Client");
        gameObject.AddComponent<NetworkManager>();
        gameObject.AddComponent<WorldManager>();
        gameObject.AddComponent<LoginScreenController>();
        gameObject.AddComponent<TestClientController>();
    }

    private void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
        _worldManager = GetComponent<WorldManager>();

        // 두 클라이언트를 번갈아 조작해도 비활성 창의 패킷 처리가 계속되도록 한다.
        Application.runInBackground = true;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        Screen.SetResolution(960, 540, FullScreenMode.Windowed);
#endif
    }

    private void OnEnable()
    {
        _networkManager.EnterGameReceived += OnEnterGameReceived;
        _networkManager.PlayerEntered += OnPlayerEntered;
        _networkManager.PlayerLeft += OnPlayerLeft;
        _networkManager.PlayerMoved += OnPlayerMoved;
        _networkManager.PlayerChatReceived += OnPlayerChatReceived;
        _networkManager.MapChanged += OnMapChanged;
        _networkManager.GameDisconnected += OnGameDisconnected;
    }

    private void OnDisable()
    {
        _networkManager.EnterGameReceived -= OnEnterGameReceived;
        _networkManager.PlayerEntered -= OnPlayerEntered;
        _networkManager.PlayerLeft -= OnPlayerLeft;
        _networkManager.PlayerMoved -= OnPlayerMoved;
        _networkManager.PlayerChatReceived -= OnPlayerChatReceived;
        _networkManager.MapChanged -= OnMapChanged;
        _networkManager.GameDisconnected -= OnGameDisconnected;
    }

    private void Update()
    {
        if (!_inGame || _changingMap || _worldManager.LocalPlayer == null || !Application.isFocused || GUIUtility.keyboardControl != 0)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        Vector2 direction = Vector2.zero;

        if (keyboard.leftArrowKey.isPressed)
            direction.x -= 1f;

        if (keyboard.rightArrowKey.isPressed)
            direction.x += 1f;

        if (keyboard.downArrowKey.isPressed)
            direction.y -= 1f;

        if (keyboard.upArrowKey.isPressed)
            direction.y += 1f;

        if (direction.sqrMagnitude > 1f)
            direction.Normalize();

        _moveTarget += direction * ArrowMoveSpeed * Time.deltaTime;

        if (_sendingMove || Time.unscaledTime < _nextMoveSendTime)
            return;

        int x = Mathf.RoundToInt(_moveTarget.x);
        int y = Mathf.RoundToInt(_moveTarget.y);

        if (x == _worldManager.LocalPlayer.ServerX && y == _worldManager.LocalPlayer.ServerY)
            return;

        _nextMoveSendTime = Time.unscaledTime + MoveSendInterval;
        SendMoveAsync(x, y, false);
    }

    private void OnGUI()
    {
        if (!_inGame)
            return;

        if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "MapChatInput")
        {
            SendChat();
            Event.current.Use();
        }

        DrawPlayerLabels();
        Rect panel = new(10, 10, 370, Screen.height - 205);
        Rect chatPanel = new(10, Screen.height - 185, 430, 175);

        if (Event.current.type == EventType.MouseDown && !panel.Contains(Event.current.mousePosition) && !chatPanel.Contains(Event.current.mousePosition))
            GUI.FocusControl(null);

        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Label("MyMMORPG Test Client");
        GUILayout.Label(_status);
        GUILayout.Label($"Map: {_currentMapId}  Local: {_worldManager.LocalCharacterId}");
        GUILayout.Label($"Remote players: {_worldManager.RemotePlayerCount}  Monsters: {_worldManager.MonsterCount}");

        if (_worldManager.LocalPlayer != null)
            GUILayout.Label($"Local position: ({_worldManager.LocalPlayer.ServerX}, {_worldManager.LocalPlayer.ServerY})");

        foreach (PlayerView player in _worldManager.RemotePlayers)
            GUILayout.Label($"Remote {player.CharacterId}: ({player.ServerX}, {player.ServerY})");

        GUILayout.Label(_lastPlayerMove);
        GUILayout.Label("Arrow keys: move (click game view)");

        GUI.enabled = !_changingMap && !_sendingMove;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Move X/Y", GUILayout.Width(70));
        _moveX = GUILayout.TextField(_moveX);
        _moveY = GUILayout.TextField(_moveY);
        if (GUILayout.Button("Send", GUILayout.Width(55)))
        {
            GUI.FocusControl(null);
            SendMove();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Map ID", GUILayout.Width(70));
        _mapId = GUILayout.TextField(_mapId);
        if (GUILayout.Button("Change", GUILayout.Width(65)))
        {
            GUI.FocusControl(null);
            ChangeMap();
        }
        GUILayout.EndHorizontal();
        GUI.enabled = true;
        GUILayout.EndArea();

        DrawChatPanel(chatPanel);
    }

    private void DrawChatPanel(Rect panel)
    {
        if (_chatMessageStyle == null)
        {
            _chatMessageStyle = new GUIStyle(GUI.skin.label);
            _chatMessageStyle.wordWrap = true;
        }

        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Label("Map Chat");
        _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(110f));

        foreach (string message in _chatMessages)
            GUILayout.Label(message, _chatMessageStyle);

        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        GUI.enabled = !_sendingChat;
        GUI.SetNextControlName("MapChatInput");
        _chatInput = GUILayout.TextField(_chatInput, GameProtocol.MaxChatMessageLength);

        if (GUILayout.Button("Send", GUILayout.Width(55f)))
            SendChat();

        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawPlayerLabels()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        if (_playerLabelStyle == null)
        {
            _playerLabelStyle = new GUIStyle(GUI.skin.label);
            _playerLabelStyle.alignment = TextAnchor.MiddleCenter;
            _playerLabelStyle.fontStyle = FontStyle.Bold;
            _playerLabelStyle.normal.textColor = Color.white;
        }

        if (_worldManager.LocalPlayer != null)
            DrawPlayerLabel(camera, _worldManager.LocalPlayer);

        foreach (PlayerView player in _worldManager.RemotePlayers)
            DrawPlayerLabel(camera, player);
    }

    private void DrawPlayerLabel(Camera camera, PlayerView player)
    {
        Vector3 screenPosition = camera.WorldToScreenPoint(player.transform.position);
        if (screenPosition.z < 0f)
            return;

        Rect label = new(screenPosition.x - 35f, Screen.height - screenPosition.y - 42f, 70f, 20f);
        GUI.Label(label, player.CharacterId.ToString(), _playerLabelStyle);
    }

    private void OnEnterGameReceived(EnterGameData data)
    {
        _inGame = data.Result == EnterGameResult.Success;
        _changingMap = false;
        _currentMapId = _inGame ? 100000000u : 0u;
        _lastPlayerMove = "Last PlayerMove: none";
        _moveTarget = new Vector2(data.X, data.Y);
        _nextMoveSendTime = 0f;
        ++_moveVersion;
        _status = _inGame ? $"Entered as {data.Name} (ID {data.CharacterId})." : $"EnterGame failed: {data.Result}";

        if (_inGame)
        {
            _playerNames.Clear();
            _playerNames[data.CharacterId] = data.Name;
            _chatMessages.Clear();
        }
    }

    private void OnPlayerEntered(PlayerEnterData data)
    {
        _playerNames[data.CharacterId] = data.Name;
    }

    private void OnPlayerLeft(uint characterId)
    {
        _playerNames.Remove(characterId);
    }

    private void OnPlayerMoved(PlayerMoveData data)
    {
        _lastPlayerMove = $"Last PlayerMove: {data.CharacterId} ({data.X}, {data.Y})";
    }

    private void OnPlayerChatReceived(PlayerChatData data)
    {
        string name = _playerNames.TryGetValue(data.CharacterId, out string playerName) ? playerName : data.CharacterId.ToString();
        string message = data.Message.Replace('\r', ' ').Replace('\n', ' ');
        _chatMessages.Add($"{name}: {message}");

        if (_chatMessages.Count > 40)
            _chatMessages.RemoveAt(0);

        _chatScroll.y = float.MaxValue;
    }

    private async void SendChat()
    {
        if (!_inGame || _sendingChat)
            return;

        string message = _chatInput.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length == 0)
        {
            _chatInput = "";
            return;
        }

        if (Encoding.UTF8.GetByteCount(message) >= GameProtocol.MaxChatMessageLength)
        {
            _status = "Chat message is too long (maximum 127 UTF-8 bytes).";
            return;
        }

        _sendingChat = true;
        _chatInput = "";
        GUI.FocusControl(null);

        try
        {
            await _networkManager.SendChatAsync(message);
        }
        catch (Exception exception)
        {
            _chatInput = message;
            _status = $"Chat failed: {exception.Message}";
        }
        finally
        {
            _sendingChat = false;
        }
    }

    private void SendMove()
    {
        if (!int.TryParse(_moveX, out int x) || !int.TryParse(_moveY, out int y))
        {
            _status = "Invalid move coordinates.";
            return;
        }

        _moveTarget = new Vector2(x, y);
        _nextMoveSendTime = Time.unscaledTime + MoveSendInterval;
        SendMoveAsync(x, y, true);
    }

    private async void SendMoveAsync(int x, int y, bool showStatus)
    {
        _sendingMove = true;
        int moveVersion = _moveVersion;

        try
        {
            await _networkManager.SendMoveAsync(x, y);

            if (moveVersion != _moveVersion || !_inGame)
                return;

            _worldManager.SetLocalTargetPosition(x, y);

            if (showStatus)
                _status = $"Sent MoveRequest ({x}, {y}).";
        }
        catch (Exception exception)
        {
            if (moveVersion == _moveVersion)
            {
                if (_worldManager.LocalPlayer != null)
                    _moveTarget = new Vector2(_worldManager.LocalPlayer.ServerX, _worldManager.LocalPlayer.ServerY);

                _status = $"Move failed: {exception.Message}";
            }
        }
        finally
        {
            _sendingMove = false;
        }
    }

    private async void ChangeMap()
    {
        if (!uint.TryParse(_mapId, out uint mapId))
        {
            _status = "Invalid map ID.";
            return;
        }

        _changingMap = true;
        ++_moveVersion;

        try
        {
            await _networkManager.ChangeMapAsync(mapId);
            _status = $"Sent ChangeMapRequest ({mapId}).";
        }
        catch (Exception exception)
        {
            _changingMap = false;
            if (_worldManager.LocalPlayer != null)
                _moveTarget = new Vector2(_worldManager.LocalPlayer.ServerX, _worldManager.LocalPlayer.ServerY);

            _status = $"Map change failed: {exception.Message}";
        }
    }

    private void OnMapChanged(ChangeMapData data)
    {
        _changingMap = false;
        _nextMoveSendTime = 0f;

        if (data.Result == ChangeMapResult.Success)
        {
            _moveTarget = new Vector2(data.X, data.Y);
            _currentMapId = data.MapId;
            _lastPlayerMove = "Last PlayerMove: none";
            string localName = _playerNames.TryGetValue(_worldManager.LocalCharacterId, out string name) ? name : _worldManager.LocalCharacterId.ToString();
            _playerNames.Clear();
            _playerNames[_worldManager.LocalCharacterId] = localName;
            _chatMessages.Clear();
        }
        else if (_worldManager.LocalPlayer != null)
        {
            _moveTarget = new Vector2(_worldManager.LocalPlayer.ServerX, _worldManager.LocalPlayer.ServerY);
        }

        _status = data.Result == ChangeMapResult.Success ? $"Changed to map {data.MapId} at ({data.X}, {data.Y})." : $"Map change failed: {data.Result}";
    }

    private void OnGameDisconnected()
    {
        _inGame = false;
        _changingMap = false;
        _sendingChat = false;
        _moveTarget = Vector2.zero;
        ++_moveVersion;
        _currentMapId = 0;
        _lastPlayerMove = "Last PlayerMove: none";
        _playerNames.Clear();
        _chatMessages.Clear();
        _chatInput = "";
        _status = "GameServer disconnected.";
    }
}
