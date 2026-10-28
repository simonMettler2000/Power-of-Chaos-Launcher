#pragma once
#include <stddef.h>
/* Shared project-authored strict master admission for GDI and D3D9 adapters.
   Supports the metadata-free master profile: complete <=16MiB 800x1160 RGB8
   PNG containing only IHDR, consecutive nonempty IDAT, then IEND. Every other
   chunk/PNG feature falls back; this is intentionally not general PNG admission.
   Validates all type bytes/reserved bit and chunk CRC/framing,
   exact complete DEFLATE extent, Adler32, scanline length/filter and PNG decode.
   Uses unchanged pinned external LodePNG with <=32MiB live decoder allocation;
   caller-owned PNG and exact-size owned IDAT allocation requests each <=16MiB.
   Decoder payload accounting excludes allocator headers and other process/WIC memory. No file paths, COM, cache,
   GPU, device ownership or renderer state. Internal allocation failure throws an
   ordinary C++ exception, reclaimed/caught at this boundary and returned false.
   Reentrant/busy decoder TLS is rejected without modifying prior state.
   Adapters must fail closed on false before WIC/cache admission. */
bool hdv_validate_master_png(const unsigned char* bytes,size_t size);

/* Same strict admission and transaction as validation; exact writable output
   span is800*1160*4 bytes, top-down opaque BGRA. Rejects null/wrong-sized or
   overlapping input/output spans. Caller owns the output and must publish it
   only on true. On ordinary rejection/allocation failure no output is written. */
bool hdv_decode_master_bgra(const unsigned char* bytes,size_t size,unsigned char* output,size_t output_size);
