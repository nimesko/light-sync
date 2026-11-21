# Security

LightSync controls hardware and runs OpenRGB with administrator rights, so security reports matter.

Please report a vulnerability privately through GitHub's **Security > Report a vulnerability** on this
repository (not a public issue). Include what you found, how to reproduce it and the LightSync
version (Help > About).

What LightSync does to stay safe:
- The service's named pipe only accepts the Windows account that runs it (and SYSTEM), and no
  remote clients.
- OpenRGB is started listening on 127.0.0.1 only; its settings folder lives under Program Files
  (administrators only), because OpenRGB runs elevated and loads plugins from there.
- The 3D preview's WebView2 may load nothing but its own bundled files.
- Key codes and sound are processed in memory only and never stored or sent.
