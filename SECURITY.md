# Security and deployment configuration

Do not commit broker passwords, API keys, Cesium tokens, SSH credentials,
private certificates, cookies, or LLM-provider credentials to this repository.

The checked-in configuration uses blank credentials or loopback defaults.
Python MQTT helpers read `FAA_MQTT_HOST`, `FAA_MQTT_PORT`,
`FAA_MQTT_USERNAME`, and `FAA_MQTT_PASSWORD` from the process environment.
Unity deployment credentials should be supplied through an approved local or
build-time secret mechanism and kept out of scenes and ScriptableObjects.

If a secret is committed, revoke or rotate it first; deleting the visible line
does not remove the value from Git history. Because this repository starts with
a sanitized snapshot, do not import unsanitized history from the former
development repository.

Report an exposure privately to the repository owner. Do not open a public
issue containing a credential or sensitive simulator endpoint.
