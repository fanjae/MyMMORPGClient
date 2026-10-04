using System.Collections.Generic;
using UnityEngine;

public sealed class WorldManager : MonoBehaviour
{
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private PlayerView playerPrefab;
    [SerializeField] private MonsterView monsterPrefab;

    private readonly Dictionary<uint, PlayerView> _players = new();
    private readonly Dictionary<uint, MonsterView> _monsters = new();

    private PlayerView _localPlayer;
    private static Sprite _testSprite;
    private static readonly Color[] PlayerColors =
    {
        new(1f, 0.4f, 0.8f),
        new(0.45f, 0.65f, 1f),
        new(0.45f, 1f, 0.5f),
        Color.cyan,
        Color.yellow,
        new(1f, 0.6f, 0.25f)
    };
    private const float CameraHeightOffset = 1f;

    public uint LocalCharacterId => _localPlayer != null ? _localPlayer.CharacterId : 0;
    public PlayerView LocalPlayer => _localPlayer;
    public IEnumerable<PlayerView> RemotePlayers => _players.Values;
    public int RemotePlayerCount => _players.Count;
    public int MonsterCount => _monsters.Count;

    private void Awake()
    {
        if (networkManager == null)
            networkManager = GetComponent<NetworkManager>();
    }

    private void OnEnable()
    {
        // 네트워크 이벤트 구독
        networkManager.EnterGameReceived += OnEnterGame;
        networkManager.PlayerEntered += OnPlayerEntered;
        networkManager.PlayerLeft += OnPlayerLeft;
        networkManager.PlayerMoved += OnPlayerMoved;
        networkManager.MonsterEntered += OnMonsterEntered;
        networkManager.MapChanged += OnMapChanged;
        networkManager.GameDisconnected += OnGameDisconnected;
    }

    private void OnDisable()
    {
        // 오브젝트 비활성화 시 네트워크 이벤트 구독 해제
        networkManager.EnterGameReceived -= OnEnterGame;
        networkManager.PlayerEntered -= OnPlayerEntered;
        networkManager.PlayerLeft -= OnPlayerLeft;
        networkManager.PlayerMoved -= OnPlayerMoved;
        networkManager.MonsterEntered -= OnMonsterEntered;
        networkManager.MapChanged -= OnMapChanged;
        networkManager.GameDisconnected -= OnGameDisconnected;
    }

    private void OnEnterGame(EnterGameData data)
    {
        if (data.Result != EnterGameResult.Success)
            return;

        // 입장 성공 시 로컬 플레이어 생성
        ClearRemoteObjects();

        if (_localPlayer != null)
            Destroy(_localPlayer.gameObject);

        _localPlayer = CreatePlayer(data.CharacterId);
        _localPlayer.Initialize(data.CharacterId, data.X, data.Y);
    }

    private void OnPlayerEntered(PlayerEnterData data)
    {
        if (data.CharacterId == LocalCharacterId || _players.ContainsKey(data.CharacterId))
            return;

        // 같은 맵에 진입한 다른 플레이어 생성
        PlayerView player = CreatePlayer(data.CharacterId);
        player.Initialize(data.CharacterId, data.X, data.Y);

        _players.Add(data.CharacterId, player);
    }

    private void OnPlayerLeft(uint characterId)
    {
        if (!_players.Remove(characterId, out PlayerView player))
            return;

        Destroy(player.gameObject);
    }

    private void OnPlayerMoved(PlayerMoveData data)
    {
        if (!_players.TryGetValue(data.CharacterId, out PlayerView player))
            return;

        player.SetTargetPosition(data.X, data.Y);
    }

    private void OnMonsterEntered(MonsterEnterData data)
    {
        if (_monsters.ContainsKey(data.MonsterId))
            return;

        // 서버에서 전달된 몬스터 생성
        MonsterView monster = CreateMonster(data.MonsterId);
        monster.Initialize(data.MonsterId, data.X, data.Y);

        _monsters.Add(data.MonsterId, monster);
    }

    private void OnMapChanged(ChangeMapData data)
    {
        if (data.Result != ChangeMapResult.Success)
            return;

        // 맵 이동 시 기존 원격 오브젝트를 정리하고 로컬 플레이어 위치 갱신
        ClearRemoteObjects();

        if (_localPlayer != null)
            _localPlayer.SetPosition(data.X, data.Y);
    }

    private void ClearRemoteObjects()
    {
        // 현재 맵에서 관리하던 원격 플레이어와 몬스터 제거
        foreach (PlayerView player in _players.Values)
            Destroy(player.gameObject);

        foreach (MonsterView monster in _monsters.Values)
            Destroy(monster.gameObject);

        _players.Clear();
        _monsters.Clear();
    }

    private void OnGameDisconnected()
    {
        ClearRemoteObjects();

        if (_localPlayer != null)
            Destroy(_localPlayer.gameObject);

        _localPlayer = null;
    }

    public void SetLocalTargetPosition(int x, int y)
    {
        if (_localPlayer != null)
            _localPlayer.SetTargetPosition(x, y);
    }

    public void PredictLocalPosition(int x, int y)
    {
        if (_localPlayer != null)
            _localPlayer.PredictPosition(x, y);
    }

    private PlayerView CreatePlayer(uint characterId)
    {
        if (playerPrefab != null)
            return Instantiate(playerPrefab);

        GameObject gameObject = CreateTestObject($"Player {characterId}", GetPlayerColor(characterId));
        return gameObject.AddComponent<PlayerView>();
    }

    private static Color GetPlayerColor(uint characterId)
    {
        // 같은 Character ID는 어느 클라이언트에서 생성해도 같은 색상을 사용한다.
        uint hash = unchecked(characterId * 2654435761u);
        int index = (int)(((ulong)hash * (ulong)PlayerColors.Length) >> 32);
        return PlayerColors[index];
    }

    private MonsterView CreateMonster(uint monsterId)
    {
        if (monsterPrefab != null)
            return Instantiate(monsterPrefab);

        GameObject gameObject = CreateTestObject($"Monster {monsterId}", Color.red);
        return gameObject.AddComponent<MonsterView>();
    }

    private static GameObject CreateTestObject(string name, Color color)
    {
        if (_testSprite == null)
        {
            Texture2D texture = new(1, 1);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _testSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }

        GameObject gameObject = new(name);
        SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = _testSprite;
        renderer.color = color;
        gameObject.transform.localScale = new Vector3(0.6f, 0.6f, 1f);
        return gameObject;
    }

    private void LateUpdate()
    {
        if (_localPlayer == null || Camera.main == null)
            return;

        Vector3 position = _localPlayer.transform.position;
        // 로컬 플레이어를 화면 중앙보다 약간 아래에 두고 주변 Map을 함께 표시한다.
        Camera.main.transform.position = new Vector3(position.x, position.y + CameraHeightOffset, -10f);
    }

    public static Vector3 ToUnityPosition(int x, int y)
    {
        // 서버의 2차원 좌표를 Unity XY 평면 좌표로 변환
        return new Vector3(x * 0.05f, y * 0.05f, 0f);
    }
}
