namespace CsiProMaxPlugin;

/// <summary>
/// What the installed plug-in calls in a newer copy of this file (<see cref="Newest"/>): the copy serves the program's
/// model and opens the window in the installed plug-in's place. The names and parameters here are found by name from
/// every older plug-in, so they must stay as they are; add a method rather than change one.
/// </summary>
public static class Live
{
    /// <summary>Serves the program's model on the plug-in's pipe. It never throws.</summary>
    /// <param name="renew">The installed plug-in's own <see cref="Newest.Renew"/>, asked when a window wants the newest plug-in.</param>
    public static void Serve(object model, Func<bool> renew)
    {
        ApiBridge.Renewer = renew;
        ApiBridge.Serve(model);
    }

    /// <summary>Opens the window from the installed folder. It throws what went wrong, for the plug-in to say.</summary>
    public static void Start(string pluginFolder) => Launcher.Start(pluginFolder);

    /// <summary>Gives the pipe up, for a newer copy still.</summary>
    public static void Stop() => ApiBridge.Stop();
}
