PhoneDrive Windows Commander layout adaptation

The panel/toolbar/path/status structure is adapted from WinSCP's
source/forms/ScpCommander.dfm (WinSCP, Martin Prikryl and contributors).
Original resource and license: third_party/references/winscp/.
Source: https://github.com/winscp/winscp/blob/master/source/forms/ScpCommander.dfm

2026-11-01: reimplemented for WPF with PhoneDrive's device, provider, transfer,
pairing and drive services; native Windows extension icons; original PhoneDrive
application artwork. No WinSCP brand artwork or WinSCP icon-set images are used.

The Windows adaptation and combined Windows application are provided under GPLv3;
see COPYING. Full Windows source is supplied. Third-party libraries retain their
own licenses. No changes are made to WinSCP itself.

QRCoder 1.6.0 generates the pairing QR image using the portable net6.0 encoder.
Copyright (c) 2013-2018 Raffael Herrmann, MIT License.
See third_party/licenses/QRCoder-1.6.0.txt (also supplied with the binaries).
