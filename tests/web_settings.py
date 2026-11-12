"""Build isolated unified server settings for local fixtures."""
def document(connection, password="", credentials=None, recording=None):
    return {"Version": 1, "ActiveConfigurationId": connection.get("ConfigurationId", "default"),
            "Configurations": [{"Connection": connection, "Password": password,
                                "Credentials": credentials or {}, "Recording": recording}],
            "Integrations": {"Enabled": False}}
