// -----------------------------------------------------------------------
// <copyright file="ServiceIdentityProjectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using OpenTelemetry.Resources;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ServiceIdentityProjectionTests
{
    private static Resource ResourceWith(params (string Key, string Value)[] attributes)
        => ResourceBuilder.CreateEmpty()
            .AddAttributes(attributes.Select(a =>
                new KeyValuePair<string, object>(a.Key, a.Value)))
            .Build();

    [Fact]
    public void Projects_all_service_attributes_from_the_resource()
    {
        var resource = ResourceWith(
            ("service.name", "billing-agent"),
            ("service.namespace", "ops"),
            ("service.instance.id", "host-7:1234"),
            ("service.version", "1.2.3"));

        var identity = TelemetryRegistrationExtensions.ProjectServiceIdentity(resource);

        Assert.Equal("billing-agent", identity.Name);
        Assert.Equal("ops", identity.Namespace);
        Assert.Equal("host-7:1234", identity.InstanceId);
        Assert.Equal("1.2.3", identity.Version);
    }

    [Fact]
    public void Namespace_and_instance_id_are_null_when_the_resource_omits_them()
    {
        var identity = TelemetryRegistrationExtensions.ProjectServiceIdentity(
            ResourceWith(("service.name", "agent")));

        Assert.Null(identity.Namespace);
        Assert.Null(identity.InstanceId);
    }

    [Fact]
    public void Service_name_falls_back_to_netclawd_when_the_resource_omits_it()
    {
        var identity = TelemetryRegistrationExtensions.ProjectServiceIdentity(
            ResourceBuilder.CreateEmpty().Build());

        Assert.Equal("netclawd", identity.Name);
    }

    [Fact]
    public void Default_resource_carries_a_service_name_and_the_build_version()
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddAttributes([new KeyValuePair<string, object>("service.version", "9.9.9")])
            .Build();

        var identity = TelemetryRegistrationExtensions.ProjectServiceIdentity(resource);

        Assert.Equal("9.9.9", identity.Version);
        Assert.False(string.IsNullOrWhiteSpace(identity.Name));
    }
}
