using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared.DeadSpace.CCCCVars;
using Content.Shared.Players.JobWhitelist;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Serilog;

namespace Content.Server.Players.JobWhitelist;

public sealed class JobWhitelistManager : IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly UserDbDataManager _userDb = default!;

    private readonly Dictionary<NetUserId, HashSet<string>> _whitelists = new();

    // DS14-start
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    // DS14-end

    public void Initialize()
    {
        _net.RegisterNetMessage<MsgJobWhitelist>();
    }

    private async Task LoadData(ICommonSession session, CancellationToken cancel)
    {
        var whitelists = await _db.GetJobWhitelists(session.UserId, cancel);
        cancel.ThrowIfCancellationRequested();
        _whitelists[session.UserId] = whitelists.ToHashSet();

        await LoadExternalWhitelist(session, cancel); // DS14
    }

    // DS14-start
    private async Task LoadExternalWhitelist(ICommonSession session, CancellationToken cancel)
    {
        var apiUrl = _config.GetCVar(CCCCVars.JobWhitelistApiUrl);
        var serverKey = _config.GetCVar(CCCCVars.JobWhitelistServerKey);

        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(serverKey))
            return;

        if (!_whitelists.TryGetValue(session.UserId, out var whitelist))
            return;

        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            cancel.ThrowIfCancellationRequested();

            if (!job.Whitelisted)
                continue;

            if (whitelist.Contains(job.ID))
                continue;

            var allowed = await CheckApi(session.Name, apiUrl, serverKey, cancel);
            if (allowed)
                whitelist.Add(job.ID);
        }
    }

    private async Task<bool> CheckApi(
        string siKey,
        string apiUrl,
        string serverKey,
        CancellationToken cancel)
    {
        var body = JsonSerializer.Serialize(new
        {
            ss14UserId = siKey
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("X-Server-Key", serverKey);

        try
        {
            using var response = await Http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(cancel);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("allowed", out var allowed)
                   && allowed.GetBoolean();
        }
        catch (Exception e)
        {
            Log.Error(e, "DS14 JobWhitelist API error for {User}", siKey);
            return false;
        }
    }
    // DS14-end

    private void FinishLoad(ICommonSession session)
    {
        SendJobWhitelist(session);
    }

    private void ClientDisconnected(ICommonSession session)
    {
        _whitelists.Remove(session.UserId);
    }

    public async void AddWhitelist(NetUserId player, ProtoId<JobPrototype> job)
    {
        if (_whitelists.TryGetValue(player, out var whitelists))
            whitelists.Add(job);

        await _db.AddJobWhitelist(player, job);

        if (_player.TryGetSessionById(player, out var session))
            SendJobWhitelist(session);
    }

    /// <summary>
    /// Returns false if role whitelist is required but the player does not have it.
    /// </summary>
    public bool IsAllowed(ICommonSession session, ProtoId<JobPrototype> job)
    {
        if (!_config.GetCVar(CCVars.GameRoleWhitelist))
            return true;

        if (!_prototypes.Resolve(job, out var jobPrototype) ||
            !jobPrototype.Whitelisted)
        {
            return true;
        }

        return IsWhitelisted(session.UserId, job);
    }

    public bool IsWhitelisted(NetUserId player, ProtoId<JobPrototype> job)
    {
        if (!_whitelists.TryGetValue(player, out var whitelists))
        {
            Log.Error("Unable to check if player {Player} is whitelisted for {Job}. Stack trace:\\n{StackTrace}",
                player,
                job,
                Environment.StackTrace);
            return false;
        }

        return whitelists.Contains(job);
    }

    public async void RemoveWhitelist(NetUserId player, ProtoId<JobPrototype> job)
    {
        _whitelists.GetValueOrDefault(player)?.Remove(job);
        await _db.RemoveJobWhitelist(player, job);

        if (_player.TryGetSessionById(new NetUserId(player), out var session))
            SendJobWhitelist(session);
    }

    public void SendJobWhitelist(ICommonSession player)
    {
        var msg = new MsgJobWhitelist
        {
            Whitelist = _whitelists.GetValueOrDefault(player.UserId) ?? new HashSet<string>()
        };

        _net.ServerSendMessage(msg, player.Channel);
    }

    void IPostInjectInit.PostInject()
    {
        _userDb.AddOnLoadPlayer(LoadData);
        _userDb.AddOnFinishLoad(FinishLoad);
        _userDb.AddOnPlayerDisconnect(ClientDisconnected);
    }
}
