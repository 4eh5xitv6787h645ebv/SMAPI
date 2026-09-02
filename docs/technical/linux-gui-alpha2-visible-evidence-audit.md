# Linux GUI alpha.2 visible-evidence audit

This is the sanitized audit record for the exact public Linux GUI alpha.2 package. It records why that package is not being used as the final screenshot source without publishing private game data, fixture data, filesystem identities, or pre-correction captures.

## Audited release

- Tag: [`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2)
- Source commit: [`052699e8ccba0d13f9d4f02e0bb199aa04cec605`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/052699e8ccba0d13f9d4f02e0bb199aa04cec605)
- Release version: `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2`
- Package qualification: the freshly downloaded six-asset release passed the immutable-inventory, checksum, metadata, manifest, attestation, package-structure, packaged-GUI, and disposable lifecycle checks recorded in the [umbrella issue](https://github.com/4eh5xitv6787h645ebv/SMAPI/issues/168#issuecomment-5515036792).

The visible workflow was then audited in isolated qualified GNOME and KDE desktop sessions. The audit did not modify the live game installation, live `Mods` directory, or live saves. It did not use the private trusted workload and did not retain any private modpack, save, game path, user name, log, or fixture identity in this repository.

## Outcome

The package qualification remains valid, but alpha.2 is not accepted as the final screenshot source. The audit found that several real states did not yet expose enough bounded, understandable evidence for their required documentation screenshot. The pre-correction captures therefore do not satisfy the final 57-image matrix and are not published as final evidence.

The 19 confirmed evidence gaps were:

- Release and transport failures: [R6](linux-gui-screenshot-evidence.md#evidence-r6), [R7](linux-gui-screenshot-evidence.md#evidence-r7), and [E1](linux-gui-screenshot-evidence.md#evidence-e1).
- Install, update, repair, uninstall, backup, and rollback plan/result evidence: [I1](linux-gui-screenshot-evidence.md#evidence-i1), [I2](linux-gui-screenshot-evidence.md#evidence-i2), [I4](linux-gui-screenshot-evidence.md#evidence-i4), [U1](linux-gui-screenshot-evidence.md#evidence-u1), [U2](linux-gui-screenshot-evidence.md#evidence-u2), [U3](linux-gui-screenshot-evidence.md#evidence-u3), [P1](linux-gui-screenshot-evidence.md#evidence-p1), [P3](linux-gui-screenshot-evidence.md#evidence-p3), [P4](linux-gui-screenshot-evidence.md#evidence-p4), [N1](linux-gui-screenshot-evidence.md#evidence-n1), [N3](linux-gui-screenshot-evidence.md#evidence-n3), [B1](linux-gui-screenshot-evidence.md#evidence-b1), [B2](linux-gui-screenshot-evidence.md#evidence-b2), and [L2](linux-gui-screenshot-evidence.md#evidence-l2).
- Diagnostic boundary: [G1](linux-gui-screenshot-evidence.md#evidence-g1).
- Manual installation help: [M1](linux-gui-screenshot-evidence.md#evidence-m1).

[A3](linux-gui-screenshot-evidence.md#evidence-a3) and [A4](linux-gui-screenshot-evidence.md#evidence-a4) were capture-ready in the alpha.2 audit, so they are not included in the 19 gaps. The new plan and result evidence materially increases page height, so the corrective release must nevertheless reconfirm the 420-DIP/200% boundary and every required scale before final capture.

## Corrective release boundary

The plan/result, recovery-capacity, managed-path, launcher-classification, durable-result, and narrow/high-scale corrections are tracked in [PR #254](https://github.com/4eh5xitv6787h645ebv/SMAPI/pull/254). Typed release-verification failures, the diagnostic boundary, and expanded manual help remain separate corrective slices so each can be reviewed and qualified independently.

After those corrections merge, a replacement exact-commit prerelease must pass the clean isolated public-package qualification again. Only that corrected public package may supply the final authentic screenshot matrix. Every retained source image must then pass the filename, pixel-hash, environment-metadata, capture-method, and original-resolution privacy review defined by the [screenshot evidence contract](linux-gui-screenshot-evidence.md).
