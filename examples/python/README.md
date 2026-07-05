# Agent Recorder Python Example

`recorder_client.py` is a small API client for local AI-agent experiments. It
uses the quick recording endpoint by default.

## Install

```bash
pip install requests
```

## Start Agent Recorder

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json
```

Then make sure the API key is available either through
`AGENT_RECORDER_API_KEY` or `.local-data\config\api-key.txt`.

## Usage

```bash
# selected-region recording for 30 seconds
python recorder_client.py --target selected_region --duration 30

# active-window recording for 60 seconds
python recorder_client.py --target active_window --duration 60

# primary-display recording for 5 minutes
python recorder_client.py --target primary_display --duration 300
```

The client prints the quick API response, waits for local confirmation, polls
recording status, and prints the final output path.

## Safety

The Python client does not approve confirmations. The local user must select any
region and approve recording through Agent Recorder UI.
