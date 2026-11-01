using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Model.Sync;
using EmbyLogger = MediaBrowser.Model.Logging.ILogger;

namespace RemoteTranscode.EmbyPlugin;

/// <summary>
/// Length of Emby's conversion queue (items queued or converting, every job together), for the workers' window.
/// Emby's sync manager has no public interface: it is looked up by name in Emby's own assembly and resolved from
/// Emby's container, so the plugin keeps loading on an Emby without it, or with a different one; the count is then
/// simply unknown (null).
/// </summary>
public sealed class SyncQueue(IServerApplicationHost host, EmbyLogger logger)
{
    private const string ManagerType = "Emby.Server.Sync.SyncManager";
    private const string QueryType = "Emby.Server.Sync.Model.SyncJobItemQuery";

    private object? manager;
    private MethodInfo? getJobItems;
    private Type? queryType;
    private bool unavailable;

    public int? Remaining()
    {
        if (unavailable)
            return null;
        try
        {
            if (manager is null && !Bind())
                return null;
            object query = Activator.CreateInstance(queryType!)!;
            queryType!.GetProperty("Statuses")!.SetValue(query, new[] { SyncJobItemStatus.Queued, SyncJobItemStatus.Converting });
            queryType.GetProperty("EnableTotalRecordCount")?.SetValue(query, true);
            queryType.GetProperty("Limit")?.SetValue(query, 1);
            object? result = getJobItems!.Invoke(manager, [query, CancellationToken.None]);
            if (result?.GetType().GetProperty("TotalRecordCount")?.GetValue(result) is int total)
                return total;
            return (result?.GetType().GetProperty("Items")?.GetValue(result) as ICollection)?.Count;
        }
        catch (Exception ex)
        {
            // Once: a different Emby will not get better at the next call.
            unavailable = true;
            logger.Info("RemoteTranscode: Emby's conversion queue cannot be read ({0}); the workers will not show it", (ex.InnerException ?? ex).Message);
            return null;
        }
    }

    private bool Bind()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var type = assemblies.Select(a => a.GetType(ManagerType, false)).FirstOrDefault(t => t != null);
        queryType = assemblies.Select(a => a.GetType(QueryType, false)).FirstOrDefault(t => t != null);
        getJobItems = type?.GetMethod("GetJobItems", [queryType ?? typeof(object), typeof(CancellationToken)]);
        if (type is null || queryType is null || getJobItems is null)
            throw new MissingMemberException(ManagerType, "GetJobItems");
        // The instance Emby itself uses: its static "Current" (Emby 4.10 does not register it in its container), else
        // what the container holds; TryResolve never creates a new one.
        manager = type.GetField("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? type.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (manager is null)
        {
            var resolve = host.GetType().GetMethod("TryResolve", Type.EmptyTypes) ?? typeof(MediaBrowser.Common.IApplicationHost).GetMethod("TryResolve")!;
            manager = resolve.MakeGenericMethod(type).Invoke(host, null);
        }
        // Not created yet (Emby still starting): asked again at the next call.
        return manager != null;
    }
}
