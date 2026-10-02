using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class TestClientController : MonoBehaviour
{
    private NetworkManager _networkManager;
    private WorldManager _worldManager;

    private string _host = "127.0.0.1";
    private string _loginPort = "7776";
    private string _loginId = "test";
    private string _password = "test1234";
    private string _moveX = "120";
    private string _moveY = "45";
    private string _mapId = "100000001";
    private string _status = "Connect to LoginServer.";
    private string _lastPlayerMove = "Last PlayerMove: none";
    private GUIStyle _playerLabelStyle;

    private const float ArrowMoveSpeed = 80f;
    private const float MoveSendInterval = 0.05f;

    private CharacterInfo[] _characters = Array.Empty<CharacterInfo>();
    private uint _currentMapId;
    private bool _connecting;
    private bool _inGame;
    private bool _sendingMove;
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
        _networkManager.LoginCompleted += OnLoginCompleted;
        _networkManager.CharacterListReceived += OnCharacterListReceived;
        _networkManager.CharacterSelected += OnCharacterSelected;
        _networkManager.EnterGameReceived += OnEnterGameReceived;
        _networkManager.PlayerMoved += OnPlayerMoved;
        _networkManager.MapChanged += OnMapChanged;
        _networkManager.LoginDisconnected += OnLoginDisconnected;
        _networkManager.GameDisconnected += OnGameDisconnected;
    }

    private void OnDisable()
    {
        _networkManager.LoginCompleted -= OnLoginCompleted;
        _networkManager.CharacterListReceived -= OnCharacterListReceived;
        _networkManager.CharacterSelected -= OnCharacterSelected;
        _networkManager.EnterGameReceived -= OnEnterGameReceived;
        _networkManager.PlayerMoved -= OnPlayerMoved;
        _networkManager.MapChanged -= OnMapChanged;
        _networkManager.LoginDisconnected -= OnLoginDisconnected;
        _networkManager.GameDisconnected -= OnGameDisconnected;
    }

    private void Update()
    {
        if (!_inGame || _connecting || _changingMap || _worldManager.LocalPlayer == null || !Application.isFocused || GUIUtility.keyboardControl != 0)
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
        DrawPlayerLabels();
        Rect panel = new(10, 10, 370, Screen.height - 20);

        if (Event.current.type == EventType.MouseDown && !panel.Contains(Event.current.mousePosition))
            GUI.FocusControl(null);

        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Label("MyMMORPG Test Client");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Host", GUILayout.Width(70));
        _host = GUILayout.TextField(_host);
        GUILayout.Label("Port", GUILayout.Width(35));
        _loginPort = GUILayout.TextField(_loginPort, GUILayout.Width(55));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Login ID", GUILayout.Width(70));
        _loginId = GUILayout.TextField(_loginId);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Password", GUILayout.Width(70));
        _password = GUILayout.PasswordField(_password, '*');
        GUILayout.EndHorizontal();

        GUI.enabled = !_connecting && !_inGame;
        if (GUILayout.Button("Connect and Login"))
        {
            GUI.FocusControl(null);
            ConnectAndLogin();
        }

        GUI.enabled = !_connecting && !_inGame;
        foreach (CharacterInfo character in _characters)
        {
            if (GUILayout.Button($"Enter: {character.Name} (ID {character.CharacterId}, Lv {character.Level})"))
            {
                GUI.FocusControl(null);
                SelectCharacter(character.CharacterId);
            }
        }

        GUI.enabled = true;
        GUILayout.Label(_status);
        GUILayout.Label($"Map: {_currentMapId}  Local: {_worldManager.LocalCharacterId}");
        GUILayout.Label($"Remote players: {_worldManager.RemotePlayerCount}  Monsters: {_worldManager.MonsterCount}");

        if (_worldManager.LocalPlayer != null)
            GUILayout.Label($"Local position: ({_worldManager.LocalPlayer.ServerX}, {_worldManager.LocalPlayer.ServerY})");

        foreach (PlayerView player in _worldManager.RemotePlayers)
            GUILayout.Label($"Remote {player.CharacterId}: ({player.ServerX}, {player.ServerY})");

        GUILayout.Label(_lastPlayerMove);
        GUILayout.Label("Arrow keys: move (click game view)");

        GUI.enabled = _inGame && !_connecting && !_changingMap && !_sendingMove;
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

    private async void ConnectAndLogin()
    {
        if (!int.TryParse(_loginPort, out int port) || port < 1 || port > 65535)
        {
            _status = "Invalid LoginServer port.";
            return;
        }

        _connecting = true;
        _characters = Array.Empty<CharacterInfo>();

        try
        {
            await _networkManager.ConnectLoginServerAsync(_host, port);
            _status = "Connected. Sending LoginRequest.";
            await _networkManager.SendLoginAsync(_loginId, _password);
        }
        catch (Exception exception)
        {
            _connecting = false;
            _status = $"Login connection failed: {exception.Message}";
        }
    }

    private async void OnLoginCompleted(LoginResult result)
    {
        if (result != LoginResult.Success)
        {
            _connecting = false;
            _status = $"Login failed: {result}";
            return;
        }

        try
        {
            _status = "Login succeeded. Requesting characters.";
            await _networkManager.RequestCharacterListAsync();
        }
        catch (Exception exception)
        {
            _connecting = false;
            _status = $"Character list request failed: {exception.Message}";
        }
    }

    private void OnCharacterListReceived(CharacterListData data)
    {
        _connecting = false;
        _characters = data.Result == CharacterListResult.Success ? data.Characters : Array.Empty<CharacterInfo>();
        _status = data.Result == CharacterListResult.Success ? $"Select a character ({_characters.Length} available)." : $"Character list failed: {data.Result}";
    }

    private async void SelectCharacter(uint characterId)
    {
        _connecting = true;

        try
        {
            _status = $"Selecting character {characterId}.";
            await _networkManager.SelectCharacterAsync(characterId);
        }
        catch (Exception exception)
        {
            _connecting = false;
            _status = $"Character selection failed: {exception.Message}";
        }
    }

    private async void OnCharacterSelected(CharacterSelectData data)
    {
        if (data.Result != CharacterSelectResult.Success)
        {
            _connecting = false;
            _status = $"Character selection failed: {data.Result}";
            return;
        }

        try
        {
            _status = $"Connecting to GameServer port {data.GameServerPort}.";
            await _networkManager.ConnectGameServerAsync(_host, data.GameServerPort, data.AuthKey);
        }
        catch (Exception exception)
        {
            _connecting = false;
            _status = $"Game connection failed: {exception.Message}";
        }
    }

    private void OnEnterGameReceived(EnterGameData data)
    {
        _connecting = false;
        _inGame = data.Result == EnterGameResult.Success;
        _changingMap = false;
        _currentMapId = _inGame ? 100000000u : 0u;
        _lastPlayerMove = "Last PlayerMove: none";
        _moveTarget = new Vector2(data.X, data.Y);
        _nextMoveSendTime = 0f;
        ++_moveVersion;
        _status = _inGame ? $"Entered as {data.Name} (ID {data.CharacterId})." : $"EnterGame failed: {data.Result}";
    }

    private void OnPlayerMoved(PlayerMoveData data)
    {
        _lastPlayerMove = $"Last PlayerMove: {data.CharacterId} ({data.X}, {data.Y})";
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
        }
        else if (_worldManager.LocalPlayer != null)
        {
            _moveTarget = new Vector2(_worldManager.LocalPlayer.ServerX, _worldManager.LocalPlayer.ServerY);
        }

        _status = data.Result == ChangeMapResult.Success ? $"Changed to map {data.MapId} at ({data.X}, {data.Y})." : $"Map change failed: {data.Result}";
    }

    private void OnLoginDisconnected()
    {
        if (_inGame)
            return;

        _connecting = false;
        _characters = Array.Empty<CharacterInfo>();
        _status = "LoginServer disconnected.";
    }

    private void OnGameDisconnected()
    {
        _connecting = false;
        _inGame = false;
        _changingMap = false;
        _moveTarget = Vector2.zero;
        ++_moveVersion;
        _currentMapId = 0;
        _lastPlayerMove = "Last PlayerMove: none";
        _characters = Array.Empty<CharacterInfo>();
        _status = "GameServer disconnected.";
    }
}
