# HD card local-pack foundation

This CPU library reads a caller-selected external pack. It does not connect to a
game, choose whether a card is visible, edit an installation, or establish a
working game mod. The consumer must preserve original rendering whenever
`TryOpen` or `TryGetImage` returns false.

The manifest is a strict UTF-8 JSON object. Its only top-level properties are
`schema`, `dimensions`, `variantPolicy` and `entries`:

```json
{
  "schema": "power-of-chaos-hd-cards-v1",
  "dimensions": {
    "physicalWidth": 800,
    "physicalHeight": 1160,
    "logicalWidth": 200,
    "logicalHeight": 290
  },
  "variantPolicy": "explicit-art-id-only",
  "entries": []
}
```

Every entry must contain exactly `game`, `cardId`, `artId`, `side`, `path` and
`sha256`. Games are `yugi`, `kaiba` or `joey`; sides are `front` or `back`.
Identifiers are case-sensitive strings of 1–128 ASCII letters, digits, dots,
underscores or hyphens. The four-part key must be unique. Every variant and back
requires an explicit mapping. There is no name, index, game, variant or side
substitution. The caller controls hidden information and must request a back
instead of a front when appropriate.

`path` is relative to the manifest's directory unless the trusted caller sets
`HdCardPackOptions.AssetRootDirectory`. The manifest cannot select another root.
Paths use forward slashes, end in `.png`, and may contain ordinary spaces.
Traversal, rooted paths, Windows device names, alternate streams and filesystem
links are refused. One path may serve multiple keys only with the same lowercase
SHA-256. Missing files may be listed and will fail safely when requested.

PNG support is deliberately limited to 800×1160 RGB8, noninterlaced, lossless
images. All five PNG filters, split consecutive IDAT chunks, and stored/fixed/
dynamic DEFLATE blocks are supported. CRCs, complete chunk framing, zlib header,
DEFLATE structure and extent, expanded length and Adler checksum are checked.
Unknown critical features, alpha/transparency and animation are refused.
Ancillary color metadata is ignored: returned values are the stored RGB samples.
The same bounded file byte array is hashed and decoded.

Opening reads only the bounded manifest and path metadata. Images decode lazily;
there is no preload. The LRU is bounded by decoded resident RGB bytes. Multiple
keys for one path share one resident image. A zero-byte cache permits transient
decoding. `CacheStats` excludes manifest storage, compressed and transient decode
buffers, and images retained by callers after eviction. Callers must bound their
own retained images. A resident image is an immutable snapshot; later file
changes are observed only after eviction or opening a new pack.

Pack methods serialize decoding/cache access. Before and after reads, the library
checks all existing path ancestors for filesystem links. Files open read-only
without write/delete sharing on Windows. As with ordinary portable .NET file
APIs, these checks do not constitute a race-free filesystem capability: pack
directories must not be concurrently renamed or modified by an untrusted writer.

The only dependency is .NET 8. Console checks generate all synthetic pixels and
PNG/manifest fixtures at runtime. Run the tests with an explicit scratch path:

```text
dotnet run --project tests/PowerOfChaos.HdCards.Tests -c Release -- <scratch-directory>
dotnet run --project tests/PowerOfChaos.HdCards.Tests -c Debug -- <different-scratch-directory>
```

These checks exercise authored CPU behavior. They do not demonstrate native HD
rendering, a game seam, installed-game compatibility or card-pack publication rights.
