# Release dates

For every formal GitHub release, use the actual publication date in the app, changelog, release notes, and packaged instructions. Never substitute the development or build date.

Use release.json as the date source. Follow docs/ReleasePolicy.md and run tools/release/Verify-ReleaseDate.ps1 against the final packaged executable and the live GitHub Release before declaring publication complete.

Keep genuine build, signing, certificate, file, and verification timestamps. They are technical evidence, not the displayed version date.
