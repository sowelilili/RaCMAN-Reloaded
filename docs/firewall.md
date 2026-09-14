# The Windows firewall rule

The console streams live state to the client over UDP. Windows blocks unsolicited inbound UDP for an unsigned application, so without a rule the client falls back to polling the console over TCP, which is slower.

## What the client does

A firewall rule allows one executable, named by its full path. At every start the client reads the firewall's rules, which needs no administrator rights, and looks for one that allows the executable that is running. If there is one, nothing happens. If there is none, the client offers to add one; adding a rule is the one step that needs an administrator prompt. A rule you added through Windows' own "allow access" prompt counts the same as one the client added. A refusal is remembered for that copy of the client and not repeated.

The installer keeps the running executable at a fixed path, so an update does not invalidate the rule.

## Checking the rules

```powershell
Get-NetFirewallRule -DisplayName 'RaCMAN Reloaded (*)' | Get-NetFirewallApplicationFilter
```

The program path must be the executable that is actually running. Under the installer that is:

```
%LocalAppData%\RaCMANReloaded\current\RaCMAN.App.exe
```

It is not the launcher in the folder above it.

## Adding or removing the rule by hand

`windows-firewall.ps1` is beside the executable in a release. Run it to add the rules for that copy of the client. Run it with `-Remove` to take them away again. It only touches rules named "RaCMAN Reloaded (...)" for that executable.
