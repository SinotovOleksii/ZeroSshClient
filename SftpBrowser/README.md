Simple SftpBrowser
A minimal SFTP file browser that uses the built-in Windows OpenSSH sftp client and public-key authentication from the user profile.
Supports browsing, copying, creating folders, and deleting files/directories on both remote and local panels.
The application supports only public-key authentication, using keys stored in: %USERPROFILE%\.ssh\

To build: 
dotnet publish -c Release

To use: 
SftpBrowser.exe username@host