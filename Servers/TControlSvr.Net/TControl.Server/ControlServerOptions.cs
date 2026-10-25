using TControl.Data;

namespace TControl.Server;

/// <summary>Bound from the <c>Control</c> configuration section (appsettings + environment overrides).</summary>
public sealed class ControlServerOptions
{
    /// <summary>TCP listen port for inbound managers. Default 3615 (DEFAULT_CTL_PORT). The C++ INI default
    /// was 3616 — override via <c>Control__Port</c> to match your deployment.</summary>
    public int Port { get; set; } = 3615;

    /// <summary>Start/restart configured local processes unless an operator deliberately stopped them.</summary>
    public bool AutoStart { get; set; }

    public int RestartDelaySeconds { get; set; } = 60;
    /// <summary>Private runtime directory for owned process identities and restart intent; survives Control restarts.</summary>
    public string ProcessStateDirectory { get; set; } = "process-state";
    public byte LocalMachineId { get; set; }
    /// <summary>Local deployment directory. Uploads are disabled when empty.</summary>
    public string UploadRoot { get; set; } = "";
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How often the connector re-dials configured game servers that are not currently connected.</summary>
    public int ConnectIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Explicit game-server dial list, used when no DB topology is available (DB-less mode) or to augment
    /// it. When the DB is present, the topology (TSERVER + TMACHINE + TIPADDR) is authoritative and this may
    /// be left empty.
    /// </summary>
    public List<ConfiguredServer> Servers { get; set; } = new();

    public ControlDbOptions Db { get; set; } = new();
}

/// <summary>A game server to connect out to (DB-less configuration).</summary>
public sealed class ConfiguredServer
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public byte Group { get; set; } = 1;
    /// <summary>Server type (SVRGRP_*: 2=login, 3=world, 4=map, 8=relay).</summary>
    public byte Type { get; set; }
    public byte ServerId { get; set; } = 1;
    public string Name { get; set; } = "";
    /// <summary>Optional local executable; never supplied by a network packet.</summary>
    public LocalProcessOptions? Process { get; set; }
    public Security.PeerTlsClientOptions? PeerTls { get; set; }
}

public sealed class LocalProcessOptions
{
    public string FileName { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public List<string> Arguments { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = new();
    public int StopTimeoutSeconds { get; set; } = 45;
    /// <summary>Explicit opt-in: forcefully terminate only after graceful shutdown times out.</summary>
    public bool ForceKillOnTimeout { get; set; }
}
