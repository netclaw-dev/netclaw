## MODIFIED Requirements

### Requirement: Image entrypoint auto-starts netclawd

The image SHALL start `tini` as PID 1. Its supervisor SHALL start `netclawd` and forward a container stop signal to it. The supervisor SHALL wait for the daemon to finish graceful drain before it exits.

#### Scenario: docker run starts the daemon

- **GIVEN** the image is present locally with valid configuration and identity files
- **WHEN** an operator starts the container
- **THEN** `tini` is PID 1 and the supervisor starts `netclawd`
- **AND** the daemon binds its HTTP port within 60 seconds

#### Scenario: Pod stop preserves a resume candidate

- **GIVEN** the container has a persistent operator state volume and an eligible interrupted session
- **WHEN** the pod sends a graceful stop signal with enough termination time
- **THEN** the supervisor forwards the signal and waits for the daemon to exit
- **AND** the state volume retains the candidate for the next container start

### Requirement: Operator state mounts at /home/netclaw/.netclaw

The image SHALL declare `VOLUME /home/netclaw/.netclaw`. The volume SHALL hold identity, configuration, session data, and restart candidates. The image SHALL not include operator credentials or identity files.

#### Scenario: Operator bind-mounts an initialized home

- **GIVEN** an operator has an initialized Netclaw home on the host
- **WHEN** they mount it at `/home/netclaw/.netclaw` and start the container
- **THEN** the daemon reads identity and configuration from that directory
- **AND** it writes session state and restart candidates to the same directory

## RENAMED Requirements

- FROM: `Operator state mounts at /root/.netclaw`
- TO: `Operator state mounts at /home/netclaw/.netclaw`
