// -----------------------------------------------------------------------
// <copyright file="TuiNavigation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Termina;

namespace Netclaw.Cli.Tui;

public sealed class TuiNavigation
{
    private TerminaApplication? _application;

    internal int BackRequestsForTests { get; private set; }

    public void Attach(TerminaApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
    }

    public bool TryGoBack()
    {
        BackRequestsForTests++;
        if (_application is null)
            throw new InvalidOperationException("TUI navigation was requested before TerminaApplication was attached.");

        if (!_application.CanGoBack)
            return false;

        _application.GoBack();
        return true;
    }
}
