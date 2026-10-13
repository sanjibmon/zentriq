using System;
using System.ServiceProcess;

namespace Zentriq.Agent;

/// <summary>Start / stop / restart a Windows service on this device, as requested by an administrator in the console.</summary>
internal static class ServiceActions
{
    private static readonly string[] Protected =
    {
        "ZentriqAgent", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Winmgmt", "BrokerInfrastructure", "SystemEventsBroker",
        "Power", "PlugPlay", "gpsvc", "CryptSvc", "EventLog", "mpssvc", "BFE",
    };

    public static (bool Ok, string? State, string Message) Run(string kind, string name, string operation, TimeSpan wait)
    {
        if (kind != "service") return (false, null, "Unsupported action '" + kind + "'.");
        if (operation != "start" && Array.Exists(Protected, p => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (false, null, "'" + name + "' is protected and cannot be stopped from the console.");
        using var sc = new ServiceController(name);
        ServiceControllerStatus status;
        try { status = sc.Status; }
        catch (InvalidOperationException) { return (false, null, "Service '" + name + "' does not exist on this device."); }

        if (operation == "stop" || operation == "restart")
        {
            if (status != ServiceControllerStatus.Stopped)
            {
                if (!sc.CanStop) return (false, status.ToString(), "The service cannot be stopped (it does not accept stop requests).");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, wait);
            }
            sc.Refresh();
            if (operation == "stop") return (true, Name(sc.Status), "Service stopped.");
        }
        if (sc.StartType == ServiceStartMode.Disabled) return (false, Name(sc.Status), "The service is disabled; change its startup type first.");
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, wait);
        }
        sc.Refresh();
        return (true, Name(sc.Status), operation == "restart" ? "Service restarted." : "Service started.");
    }

    /// <summary>Same state names as Win32_Service (Running, Stopped, Start Pending...).</summary>
    private static string Name(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.StartPending => "Start Pending",
        ServiceControllerStatus.StopPending => "Stop Pending",
        ServiceControllerStatus.ContinuePending => "Continue Pending",
        ServiceControllerStatus.PausePending => "Pause Pending",
        _ => s.ToString(),
    };
}
