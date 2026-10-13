using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal sealed class OrdinaryArtifactMetadataSampler(IReadOnlyPathMetadata ancestors, IReadOnlyFileMetadataProbe leaf,Func<string,bool>? localDrive=null)
{
    internal bool SupportsDrive(string root)
    {
        try {return localDrive?.Invoke(root)??(OperatingSystem.IsWindows() &&
            new DriveInfo(root).DriveType is DriveType.Removable or DriveType.Fixed or DriveType.Ram);}
        catch {return false;}
    }
    internal static string? PathReason(string path, out string[] parents)
    {
        parents=Array.Empty<string>();
        if (path.Length>WindowsReadOnlyFileMetadataProbe.MaximumRequestLength) return "path_check_limit";
        if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path)) return "path_invalid";
        var parts=path[3..].Split('\\');
        foreach(var part in parts)
        {
            var stem=part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if(stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
               (stem.Length==4 && (stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal)) &&
                (stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'))) return "path_invalid";
        }
        var list=new List<string>{path[..3]}; var current=path[..3];
        foreach(var part in parts[..^1]){current=Path.Combine(current,part);list.Add(current);}
        parents=list.ToArray(); return parents.Length>64?"ancestor_check_limit":null;
    }

    internal OrdinaryArtifactFileCheck Sample(string name,string path,long size,PublicationIdentityAnchor? anchor,
        string[] parents,Dictionary<string,(string Observation,string Reason)?> cache,CancellationToken cancellation,bool requireSingleLink=false)
    {
        var at=DateTimeOffset.UtcNow;
        OrdinaryArtifactFileCheck Result(string observation,string reason,long? observed=null,string identity="unknown") =>
            new(name,size,observed,observation,reason,at,identity);
        foreach(var parent in parents)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!cache.TryGetValue(parent,out var failure))
            {
                try
                {
                    var attributes=ancestors.GetAttributes(parent);
                    failure=(attributes&FileAttributes.ReparsePoint)!=0?("unsafe_path","ancestor_reparse_point"):
                        (attributes&FileAttributes.Directory)==0?("unsafe_path","parent_not_directory"):
                        !WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)attributes)?("unavailable","metadata_unavailable"):null;
                }
                catch(Exception ex){failure=Error(ex,true);}
                cache.Add(parent,failure);
            }
            if(failure is { } fault) return Result(fault.Observation,fault.Reason);
        }
        cancellation.ThrowIfCancellationRequested();
        FileMetadataObservation? sample;
        try { sample=leaf.Observe(path); }
        catch(Exception ex){var fault=Error(ex,false);return Result(fault.Observation,fault.Reason);}
        if(sample is null || !Enum.IsDefined(sample.Outcome))return Result("unavailable","metadata_unavailable");
        if(sample.Outcome!=FileObservationOutcome.Sampled)
        {
            if(sample.Before is not null||sample.After is not null||sample.BeforePath is not null||sample.AfterPath is not null)
                return Result("unavailable","metadata_unavailable");
            var fault=sample.Outcome switch {
                FileObservationOutcome.Missing=>("missing","file_missing"),
                FileObservationOutcome.Inaccessible=>("inaccessible","file_inaccessible"),
                FileObservationOutcome.ReparsePoint=>("unsafe_path","leaf_reparse_point"),
                FileObservationOutcome.Directory=>("unsafe_path","output_not_file"),
                FileObservationOutcome.MetadataChanged=>("changed_during_probe","metadata_changed"),
                FileObservationOutcome.ResolvedPathUnavailable=>("unavailable","resolved_path_unavailable"),
                _=>("unavailable","metadata_unavailable")};
            return Result(fault.Item1,fault.Item2);
        }
        var first=sample.Before;var second=sample.After;
        if(first is null||second is null)return Result("unavailable","metadata_unavailable");
        if((first.Attributes&FileAttributes.ReparsePoint)!=0||(second.Attributes&FileAttributes.ReparsePoint)!=0)
            return Result("unsafe_path","leaf_reparse_point");
        if((first.Attributes&FileAttributes.Directory)!=0||(second.Attributes&FileAttributes.Directory)!=0||first.Directory==1||second.Directory==1)
            return Result("unsafe_path","output_not_file");
        if(!Valid(first)||!Valid(second))return Result("unavailable","metadata_unavailable");
        if(requireSingleLink && (first.Links!=1 || second.Links!=1))return Result("unsafe_path","multiple_links");
        if(first.DeletePending==1||second.DeletePending==1||first!=second)
            return Result("changed_during_probe","metadata_changed");
        if(!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(sample.BeforePath)||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(sample.AfterPath)||
            !string.Equals(sample.BeforePath,path,StringComparison.OrdinalIgnoreCase)||
            !string.Equals(sample.AfterPath,path,StringComparison.OrdinalIgnoreCase))
            return Result("unsafe_path","resolved_path_mismatch");
        var identity=anchor is null?"unverified":anchor.Identity==second.Identity?"match":"mismatch";
        return Result(second.Length==size?"present_size_matches":"size_mismatch",
            identity=="mismatch"?"publication_identity_mismatch":second.Length!=size?"file_size_mismatch":
            identity=="match"?"metadata_and_publication_identity_match":"size_matches_identity_unverified",second.Length,identity);
    }
    private static bool Valid(HandleMetadataSnapshot s)=>s.Length>=0&&s.AllocationSize>=0&&s.Links>0&&s.Directory<=1&&s.DeletePending<=1&&
        WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)s.Attributes)&&s.LastWriteTime is >0 and <=2650467743999999999L&&
        s.Identity is {VolumeSerialNumber:>0} id&&(id.IdLow!=0||id.IdHigh!=0);
    private static (string Observation,string Reason) Error(Exception ex,bool parent)
    {
        if(ex is FileNotFoundException or DirectoryNotFoundException) return("missing",parent?"parent_missing":"file_missing");
        if(ex is UnauthorizedAccessException || ex is IOException && (ex.HResult&0xffff) is 5 or 32 or 33)
            return("inaccessible","file_inaccessible");
        if(ex is IOException && (ex.HResult&0xffff) is 2 or 3) return("missing",parent?"parent_missing":"file_missing");
        return("unavailable","metadata_unavailable");
    }
}
