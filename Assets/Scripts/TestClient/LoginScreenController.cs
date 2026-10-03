using System;
using UnityEngine;

public sealed class LoginScreenController : MonoBehaviour
{
    private NetworkManager _networkManager;
    private string _host = "127.0.0.1";
    private string _loginPort = "7776";
    private string _loginId = "";
    private string _password = "";
    private string _status = "Enter your account to connect.";
    private CharacterInfo[] _characters = Array.Empty<CharacterInfo>();
    private bool _connecting;
    private bool _inGame;

    private void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
    }

    private void OnEnable()
    {
        _networkManager.LoginCompleted += OnLoginCompleted;
        _networkManager.CharacterListReceived += OnCharacterListReceived;
        _networkManager.CharacterSelected += OnCharacterSelected;
        _networkManager.EnterGameReceived += OnEnterGameReceived;
        _networkManager.LoginDisconnected += OnLoginDisconnected;
        _networkManager.GameDisconnected += OnGameDisconnected;
    }

    private void OnDisable()
    {
        _networkManager.LoginCompleted -= OnLoginCompleted;
        _networkManager.CharacterListReceived -= OnCharacterListReceived;
        _networkManager.CharacterSelected -= OnCharacterSelected;
        _networkManager.EnterGameReceived -= OnEnterGameReceived;
        _networkManager.LoginDisconnected -= OnLoginDisconnected;
        _networkManager.GameDisconnected -= OnGameDisconnected;
    }

    private void OnGUI()
    {
        if (_inGame)
            return;

        Rect panel = new((Screen.width - 380f) * 0.5f, (Screen.height - 310f) * 0.5f, 380f, 310f);
        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Label("MyMMORPG Login");
        GUILayout.Space(8f);

        GUILayout.Label("Login ID");
        _loginId = GUILayout.TextField(_loginId);

        GUILayout.Label("Password");
        _password = GUILayout.PasswordField(_password, '*');

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Host", GUILayout.Width(35f));
        _host = GUILayout.TextField(_host);
        GUILayout.Label("Port", GUILayout.Width(30f));
        _loginPort = GUILayout.TextField(_loginPort, GUILayout.Width(55f));
        GUILayout.EndHorizontal();

        GUI.enabled = !_connecting;
        if (_characters.Length == 0)
        {
            if (GUILayout.Button("Connect and Login"))
            {
                GUI.FocusControl(null);
                ConnectAndLogin();
            }
        }
        else
        {
            foreach (CharacterInfo character in _characters)
            {
                if (GUILayout.Button($"Enter: {character.Name} (ID {character.CharacterId}, Lv {character.Level})"))
                {
                    GUI.FocusControl(null);
                    SelectCharacter(character.CharacterId);
                }
            }
        }

        GUI.enabled = true;
        GUILayout.Label(_status);
        GUILayout.EndArea();
    }

    private async void ConnectAndLogin()
    {
        if (string.IsNullOrWhiteSpace(_loginId) || string.IsNullOrEmpty(_password))
        {
            _status = "Enter a Login ID and Password.";
            return;
        }

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
            _password = "";
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
        _status = _inGame ? "" : $"EnterGame failed: {data.Result}";
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
        _characters = Array.Empty<CharacterInfo>();
        _status = "GameServer disconnected. Connect again to continue.";
    }
}
