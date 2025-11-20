Pritunl Zero SSH Utility

A lightweight Windows-only console utility that automates obtaining SSH user certificates from Pritunl Zero, launching SSH/SFTP clients, and opening sessions with short-lived certificates.
This tool is designed for developers and administrators who use SSH certificate authentication instead of passwords, and want a simple way to:
-request a new SSH certificate from Pritunl Zero
-launch SSH sessions (WezTerm, OpenSSH, etc.)
-open an SFTP file browser (external GUI or built-in tool)

The utility manages SSH keys, interacts with Pritunl Zero’s /ssh/challenge API, and stores certificates in the standard Windows OpenSSH layout.

Configuration File (sshzero.config.json)

The configuration file is generated automatically on first launch.
Example:
{
  "ZeroBaseUrl": "https://zero.company.com",
  "UserKeyPath": "%USERPROFILE%\\.ssh\\id_ed25519",

  "SshCommand": "ssh",
  "SshCommandArgs": "{user}@{host}",

  "SftpBrowserCommand": "SftpBrowser.exe",
  "SftpBrowserArgs": "{user}@{host}",

  "Servers": [
    {
      "Name": "AppServer",
      "Host": "10.1.10.15",
      "User": "deployer"
    }
  ]
}

Config Parameters

ZeroBaseUrl:

Base URL of your Pritunl Zero portal
Example: https://zero.company.com

UserKeyPath:

Location of the Ed25519 keypair used for certificate signing.
Environment variables are supported.
Default:
%USERPROFILE%\.ssh\id_ed25519

SshCommand / SshCommandArgs:

Defines which SSH client should be launched.
Examples:
OpenSSH (default):
"SshCommand": "ssh",
"SshCommandArgs": "{user}@{host}"
Available placeholders:
{user} — SSH username
{host} — server hostname or IP
It's better to install WezTerm to use with linux ssh.

SftpBrowserCommand / SftpBrowserArgs:

External utility used as SFTP GUI browser.
Example:
"SftpBrowserCommand": "SftpBrowser.exe",
"SftpBrowserArgs": "{user}@{host}"

Servers[]:

List of SSH hosts.
Fields:
Name – label in the menu
Host – IP or DNS
User – SSH username for certificate login


Usage

Simply run:
sshzero.exe

You will see a menu with your configured servers.

The workflow is:
Select a server
Tool checks if a valid certificate exists
If not — opens browser for Pritunl Zero login
Approve SSH certificate in the browser
The tool downloads and saves the certificate

You choose:
-Open SSH session
-Open SFTP browser
-Do both

Certificate Handling
Certificates are stored in standard OpenSSH paths:
id_ed25519
id_ed25519.pub
id_ed25519-cert
id_ed25519-cert.pub

To build: dotnet publish -c Release