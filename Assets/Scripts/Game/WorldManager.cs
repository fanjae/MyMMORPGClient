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

    private void OnEnable()
    {
        // 네트워크 이벤트 구독
        networkManager.EnterGameReceived += OnEnterGame;
        networkManager.PlayerEntered += OnPlayerEntered;
        networkManager.PlayerLeft += OnPlayerLeft;
        networkManager.PlayerMoved += OnPlayerMoved;
        networkManager.MonsterEntered += OnMonsterEntered;
        networkManager.MapChanged += OnMapChanged;
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
    }

    private void OnEnterGame(EnterGameData data)
    {
        if (data.Result != EnterGameResult.Success)
            return;

        // 입장 성공 시 로컬 플레이어 생성
        _localPlayer = Instantiate(playerPrefab);
        _localPlayer.Initialize(data.CharacterId, data.X, data.Y);
    }

    private void OnPlayerEntered(PlayerEnterData data)
    {
        if (_players.ContainsKey(data.CharacterId))
            return;

        // 같은 맵에 진입한 다른 플레이어 생성
        PlayerView player = Instantiate(playerPrefab);
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

        player.SetPosition(data.X, data.Y);
    }

    private void OnMonsterEntered(MonsterEnterData data)
    {
        if (_monsters.ContainsKey(data.MonsterId))
            return;

        // 서버에서 전달된 몬스터 생성
        MonsterView monster = Instantiate(monsterPrefab);
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

    public static Vector3 ToUnityPosition(int x, int y)
    {
        // 서버의 2차원 좌표를 Unity XY 평면 좌표로 변환
        return new Vector3(x, y, 0f);
    }
}
