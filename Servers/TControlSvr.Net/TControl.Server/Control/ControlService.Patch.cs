using TControl.Protocol;

namespace TControl.Server.Control;

/// <summary>
/// Patch / preversion administration + the file-upload wire flow. The DB-side procs (TUpdateVersion,
/// TBetaToVersion, TDeletePreVersion, TUpdatePreVersion, TPREVERSION read) are fully ported; the file
/// transport commits into a configured local deployment root (the C++ wrote to an SMB admin share).
/// C++ OnCT_UPDATEPATCH_REQ / OnCT_PREVERSION* / OnCT_SERVICEUPLOAD*.
/// </summary>
public sealed partial class ControlService
{
    /// <summary>Register patch-file records via TUpdateVersion. C++ OnCT_UPDATEPATCH_REQ.</summary>
    private async Task OnCT_UPDATEPATCH_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.Control)) return;
        ushort count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            string path = r.ReadString();
            string name = r.ReadString();
            uint size = r.ReadUInt32();
            if (_db is not null)
            {
                try { await _db.UpdatePatchAsync(path, name, size, 0, CancellationToken.None); }
                catch (Exception ex) { _log.LogWarning(ex, "TUpdateVersion failed for {File}.", name); }
            }
        }
    }

    /// <summary>Return the TPREVERSION file list. C++ OnCT_PREVERSIONTABLE_REQ.</summary>
    private async Task OnCT_PREVERSIONTABLE_REQ(ManagerSession mgr)
    {
        if (!CheckAuthority(mgr, ManagerClass.Control)) return;
        mgr.Send(BuildPreVersionTable(await LoadPreVersionSafeAsync()));
    }

    /// <summary>Promote beta files to version + re-register preversion files, then return the fresh list.
    /// C++ OnCT_PREVERSIONUPDATE_REQ.</summary>
    private async Task OnCT_PREVERSIONUPDATE_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.Control)) return;

        ushort count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            uint betaVer = r.ReadUInt32();
            if (_db is not null) { try { await _db.BetaToVersionAsync(betaVer, CancellationToken.None); } catch (Exception ex) { _log.LogWarning(ex, "TBetaToVersion failed."); } }
        }

        count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            uint betaVer = r.ReadUInt32();
            if (_db is not null) { try { await _db.DeletePreVersionAsync(betaVer, CancellationToken.None); } catch (Exception ex) { _log.LogWarning(ex, "TDeletePreVersion failed."); } }
        }

        count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            string path = r.ReadString();
            string name = r.ReadString();
            uint size = r.ReadUInt32();
            if (_db is not null) { try { await _db.UpdatePrePatchAsync(path, name, size, CancellationToken.None); } catch (Exception ex) { _log.LogWarning(ex, "TUpdatePreVersion failed."); } }
        }

        mgr.Send(BuildPreVersionTable(await LoadPreVersionSafeAsync()));
    }

    /// <summary>Begin a file upload. C++ OnCT_SERVICEUPLOADSTART_REQ.</summary>
    private void OnCT_SERVICEUPLOADSTART_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.Control)) return;
        byte machineId = r.ReadByte();
        string fileName = r.ReadString();

        if (_uploadOwner is not null)
        {
            mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_ACK).WriteByte(1));
            return;
        }

        byte ret = _fileDeploy.Begin(machineId, fileName);
        if (ret != 0)
        {
            mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_ACK).WriteByte(ret).ToArray());
            return;
        }
        mgr.Upload = true;
        _uploadOwner = mgr;
        mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADSTART_ACK).WriteByte(Proto.AckSuccess).ToArray());
    }

    /// <summary>Write an upload chunk. C++ OnCT_SERVICEUPLOAD_REQ ([wSize][wSize bytes]).</summary>
    private void OnCT_SERVICEUPLOAD_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!OwnsUpload(mgr)) return;
        if (r.Remaining < 2) { FailUpload(mgr); return; }
        ushort size = r.ReadUInt16();
        if (size == 0 || r.Remaining != size)
        {
            FailUpload(mgr);
            return;
        }
        var data = r.ReadRemaining();
        byte ret = _fileDeploy.Write(data);
        if (ret != 0)
        {
            FailUpload(mgr);
            return;
        }
        mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOAD_ACK).ToArray());
    }

    /// <summary>Finish / cancel an upload. C++ OnCT_SERVICEUPLOADEND_REQ.</summary>
    private void OnCT_SERVICEUPLOADEND_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!OwnsUpload(mgr)) return;
        if (r.Remaining < 1) { FailUpload(mgr); return; }
        byte cancel = r.ReadByte();
        if (cancel != 0)
        {
            CancelUpload(mgr);
            return;
        }

        // Non-cancel END carries a trailing final chunk [wSize:WORD][wSize bytes] (C++ Handler.cpp:497-515).
        if (r.Remaining < 2) { FailUpload(mgr); return; }
        ushort size = r.ReadUInt16();
        if (r.Remaining != size) { FailUpload(mgr); return; }
        if (size != 0)
        {
            var data = r.ReadRemaining();
            if (_fileDeploy.Write(data) != 0)
            {
                FailUpload(mgr);
                return;
            }
        }

        byte ret = _fileDeploy.End(cancel: false);
        mgr.Upload = false;
        _uploadOwner = null;
        mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_ACK).WriteByte(ret).ToArray());
    }

    private bool OwnsUpload(ManagerSession mgr)
    {
        if (_uploadOwner == mgr && mgr.Upload) return true;
        mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_ACK).WriteByte(4));
        return false;
    }

    private void CancelUpload(ManagerSession mgr)
    {
        if (_uploadOwner != mgr) return;
        _fileDeploy.End(cancel: true);
        mgr.Upload = false;
        _uploadOwner = null;
    }

    private void FailUpload(ManagerSession mgr)
    {
        CancelUpload(mgr);
        mgr.Send(new PacketWriter(Msg.CT_SERVICEUPLOADEND_ACK).WriteByte(4));
    }

    private async Task<List<PatchFile>> LoadPreVersionSafeAsync()
    {
        if (_db is null) return new();
        try { return await _db.LoadPreVersionAsync(CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "TPREVERSION load failed."); return new(); }
    }

    private static byte[] BuildPreVersionTable(List<PatchFile> list)
    {
        var w = new PacketWriter(Msg.CT_PREVERSIONTABLE_ACK).WriteUInt16((ushort)list.Count);
        foreach (var p in list) { w.WriteUInt32(p.BetaVer); w.WriteString(p.Path); w.WriteString(p.Name); w.WriteUInt32(p.Size); }
        return w.ToArray();
    }
}
