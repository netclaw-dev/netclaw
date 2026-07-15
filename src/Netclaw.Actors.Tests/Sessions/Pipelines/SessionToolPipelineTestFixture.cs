// -----------------------------------------------------------------------
// <copyright file="SessionToolPipelineTestFixture.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

internal sealed class SessionToolPipelineTestFixture(
    IToolExecutor executor,
    IReadOnlyList<FunctionCallContent> toolCalls,
    SessionId sessionId,
    IActorRef replyTo)
{
    private MessageSource? _source;
    private TurnContext? _turnContext;
    private TimeProvider _timeProvider = TimeProvider.System;
    private string _sessionDirectory = Path.GetTempPath();
    private InlineOutputBudget _inlineOutputBudget = new(4096);
    private ToolExecutionTimeout _timeout = new(TimeSpan.FromSeconds(5));
    private Action<SubAgentOutput> _emitSubAgentOutput = _ => { };
    private Func<object, string, CancellationToken, Task<object>> _spawnChildActor
        = static (_, _, _) => Task.FromResult<object>(new object());
    private IApprovalChannel _approvalChannel = new ApprovalChannel();
    private Action<ToolInteractionRequestDispatch> _emitApprovalRequest = _ => { };
    private ToolExecutionTimeout _approvalTimeout = new(Timeout.InfiniteTimeSpan);
    private BackgroundJobDispatch _backgroundJobs = new BackgroundJobDispatch.Unavailable();
    private string? _projectDirectory;
    private IReadOnlyList<string> _recentFiles = [];
    private bool _setWorkingDirectoryAvailable;
    private bool _streamResults;
    private ModelModality _modelInputModalities = ModelModality.Text;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _oneTimeApprovalPreSeed
        = new Dictionary<string, IReadOnlyList<string>>();
    private IReadOnlyDictionary<string, ApprovalDecision> _decisionOverrides
        = new Dictionary<string, ApprovalDecision>();

    public SessionToolPipelineTestFixture From(MessageSource source)
    {
        _source = source;
        return this;
    }

    public SessionToolPipelineTestFixture WithTurnContext(TurnContext turnContext)
    {
        _turnContext = turnContext;
        return this;
    }

    public SessionToolPipelineTestFixture WithTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        return this;
    }

    public SessionToolPipelineTestFixture InSessionDirectory(string sessionDirectory)
    {
        _sessionDirectory = sessionDirectory;
        return this;
    }

    public SessionToolPipelineTestFixture WithInlineOutputBudget(int characters)
    {
        _inlineOutputBudget = new InlineOutputBudget(characters);
        return this;
    }

    public SessionToolPipelineTestFixture WithTimeout(TimeSpan timeout)
    {
        _timeout = new ToolExecutionTimeout(timeout);
        return this;
    }

    public SessionToolPipelineTestFixture EmittingSubAgentOutput(Action<SubAgentOutput> emit)
    {
        _emitSubAgentOutput = emit;
        return this;
    }

    public SessionToolPipelineTestFixture SpawningChildrenWith(
        Func<object, string, CancellationToken, Task<object>> spawn)
    {
        _spawnChildActor = spawn;
        return this;
    }

    public SessionToolPipelineTestFixture WithApprovals(
        IApprovalChannel channel,
        Action<ToolInteractionRequestDispatch> emitRequest,
        TimeSpan timeout)
    {
        _approvalChannel = channel;
        _emitApprovalRequest = emitRequest;
        _approvalTimeout = new ToolExecutionTimeout(timeout);
        return this;
    }

    public SessionToolPipelineTestFixture WithBackgroundJobs(IActorRef manager)
    {
        _backgroundJobs = new BackgroundJobDispatch.Available(manager);
        return this;
    }

    public SessionToolPipelineTestFixture InProject(string projectDirectory)
    {
        _projectDirectory = projectDirectory;
        return this;
    }

    public SessionToolPipelineTestFixture InProject(
        string projectDirectory,
        IReadOnlyList<string> recentFiles)
    {
        _projectDirectory = projectDirectory;
        _recentFiles = recentFiles;
        return this;
    }

    public SessionToolPipelineTestFixture WithSetWorkingDirectoryAvailable()
    {
        _setWorkingDirectoryAvailable = true;
        return this;
    }

    public SessionToolPipelineTestFixture StreamingResults()
    {
        _streamResults = true;
        return this;
    }

    public SessionToolPipelineTestFixture AcceptingModelInput(ModelModality modalities)
    {
        _modelInputModalities = modalities;
        return this;
    }

    public SessionToolPipelineTestFixture RedrivingApprovals(
        IReadOnlyDictionary<string, IReadOnlyList<string>> preSeed,
        IReadOnlyDictionary<string, ApprovalDecision> overrides)
    {
        _oneTimeApprovalPreSeed = preSeed;
        _decisionOverrides = overrides;
        return this;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var turnContext = _turnContext ?? TurnContext.FromMessageSource(
            sessionId,
            new TurnId("test-tool-batch"),
            _source);
        var runEnvironment = new SessionToolRunEnvironment
        {
            SessionDirectory = _sessionDirectory,
            InlineOutputBudget = _inlineOutputBudget,
            ModelInputModalities = _modelInputModalities,
            SpawnChildActor = _spawnChildActor,
            ProjectDirectory = _projectDirectory,
            RecentFiles = _recentFiles
        };
        var pipeline = new SessionToolExecutionPipeline(
            executor,
            _timeProvider,
            NoLogger.Instance);
        var batch = new SessionToolBatch(turnContext, runEnvironment)
        {
            ToolCalls = toolCalls,
            DefaultTimeout = _timeout,
            ReplyTo = replyTo,
            EmitSubAgentOutput = _emitSubAgentOutput,
            ApprovalRequests = new ToolApprovalRequests(
                _approvalChannel,
                _emitApprovalRequest,
                _approvalTimeout),
            BackgroundJobs = _backgroundJobs,
            SetWorkingDirectoryAvailable = _setWorkingDirectoryAvailable,
            StreamResults = _streamResults,
            OneTimeApprovalPreSeed = _oneTimeApprovalPreSeed,
            DecisionOverrides = _decisionOverrides,
            CancellationToken = cancellationToken
        };

        return pipeline.ExecuteAsync(batch);
    }
}
