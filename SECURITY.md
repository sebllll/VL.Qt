# Security policy

## Supported versions

Security fixes go into the latest release on [nuget.org](https://www.nuget.org/packages/VL.Qt) and into `develop`. Older versions get no fixes; update to the latest release.

## Report a vulnerability

Do not open a public issue for a security problem. Report it privately instead:

- on GitHub: the tab **Security** of this repository, then **Report a vulnerability**;
- or by mail to mail@schnellebuntebilder.de, subject "VL.Qt security".

Describe the problem, the affected version and, if you can, the steps or a patch that shows it. We aim to answer within a week. Once a fix is released, the advisory is published and names you, unless you ask not to be named.

VL.Qt runs QML and loads files, images and videos with the rights of the vvvv process; QML from untrusted sources can run any code that QML allows. That is by design, not a vulnerability. Problems in Qt itself belong to the [Qt project](https://wiki.qt.io/Qt_Project_Security_Policy).
