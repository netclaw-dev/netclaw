// -----------------------------------------------------------------------
// <copyright file="HeadlessTerminaFixture.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Reactive;
using Termina.Terminal;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Shared setup for headless page-level tests that drive a single-route Termina app
/// through <see cref="VirtualTerminal"/> and <see cref="VirtualInputSource"/>. Covers
/// the single-page fixture shape used by page test suites (see GitHub issue #929);
/// multi-route suites (sessions/chat/init-existing-install navigation) keep their own
/// bespoke setup because they register more than one route.
/// </summary>
internal static class HeadlessTerminaFixture
{
    public static (VirtualTerminal Terminal, TerminaApplication App, TVm Vm) Create<TPage, TVm>(
        string route,
        Func<TPage> createPage,
        Func<TVm> createViewModel,
        out VirtualInputSource input,
        int width = 120,
        int height = 40)
        where TPage : ReactivePage<TVm>
        where TVm : ReactiveViewModel
    {
        var terminal = new VirtualTerminal(width, height);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        TVm? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina(route, builder =>
        {
            builder.RegisterRoute<TPage, TVm>(
                route,
                _ => createPage(),
                _ =>
                {
                    capturedVm = createViewModel();
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
