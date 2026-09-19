// -----------------------------------------------------------------------
// <copyright file="TeamsGraphServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Channels.Teams;

namespace Netclaw.Channels.Teams.Graph;

/// <summary>
/// Composes the bounded Graph directory client with the Teams runtime without
/// exposing Graph SDK types to the daemon or channel policy projects.
/// </summary>
public static class TeamsGraphServiceCollectionExtensions
{
    public static IServiceCollection AddTeamsDirectory(this IServiceCollection services, TeamsChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var registration = TeamsIngressRegistration.Evaluate(options);
        if (registration.CanActivateSdk)
        {
            services.AddSingleton(serviceProvider =>
                TeamsGraphDirectoryClient.Create(
                    options,
                    serviceProvider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ITeamsDirectory>(serviceProvider =>
                serviceProvider.GetRequiredService<TeamsGraphDirectoryClient>());
            services.AddSingleton<ITeamsDirectoryUserCache>(serviceProvider =>
                serviceProvider.GetRequiredService<TeamsGraphDirectoryClient>());
        }

        services.AddSingleton(serviceProvider => new TeamsPrincipalAuthorizer(
            options,
            serviceProvider.GetService<ITeamsDirectory>()));
        return services;
    }
}
