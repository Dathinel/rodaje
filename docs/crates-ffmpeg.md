# Crates de Rust dentro de las DLL de FFmpeg (rav1e y librsvg)

Inventario del 12 de octubre de 2026 para la compilación `win64-lgpl-shared` de BtbN (commit `6c9aec5fc9a7`), que compila rav1e con `cargo cinstall` y librsvg con `cargo vendor` + meson; los crates quedan estáticos dentro de las DLL, así que sus avisos van en `THIRD-PARTY-NOTICES.txt` (sección 7, bloque «Crates de Rust»). El conjunto se calculó desde los `Cargo.lock` y `Cargo.toml` resolviendo features y `cfg` del target, sin dev-dependencies ni build-dependencies y con los proc-macro aparte; no se pudo correr `cargo tree` en el entorno real de BtbN, y 33 crates marcados «posible» los arrastra la feature `binaries` de rav1e aunque la biblioteca no los use. `selectors` no trae el texto de la MPL-2.0 en su tarball; el texto va con `cssparser`. Target `x86_64-pc-windows-gnu`. rav1e `31435de9d76fddd38f6dcc31d4014574cebb2092` (features por defecto + `capi`); librsvg `7612431eb02dc009319094f8513d63c1faaecfb4` (`librsvg-c` con `capi` + `avif`). Licencia según el campo `license` de crates.io (API `/api/v1/crates/<nombre>/<versión>`); repositorio según crates.io o, si la API no lo da, el `Cargo.toml` del tarball.

| Crate | Versión | Biblioteca | Licencia (SPDX de crates.io) | Repositorio | Nota |
|---|---|---|---|---|---|
| adler2 | 2.0.1 | librsvg | 0BSD OR MIT OR Apache-2.0 | https://github.com/oyvindln/adler2 |  |
| ahash | 0.8.12 | librsvg | MIT OR Apache-2.0 | https://github.com/tkaitchuck/ahash |  |
| aho-corasick | 1.1.5 | librsvg | Unlicense OR MIT | https://github.com/BurntSushi/aho-corasick |  |
| aligned | 0.4.2 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-embedded-community/aligned |  |
| aligned-vec | 0.6.4 | rav1e | MIT | https://github.com/sarah-ek/aligned-vec/ |  |
| anstream | 0.6.19 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| anstyle | 1.0.11 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| anstyle-parse | 0.2.7 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| anstyle-query | 1.1.3 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| anstyle-wincon | 3.0.9 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| anyhow | 1.0.98 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/anyhow |  |
| approx | 0.5.1 | librsvg | Apache-2.0 | https://github.com/brendanzab/approx |  |
| arg_enum_proc_macro | 0.3.4 | rav1e | MIT | https://github.com/lu-zero/arg_enum_proc_macro | proc-macro |
| arrayvec | 0.7.6 | rav1e | MIT OR Apache-2.0 | https://github.com/bluss/arrayvec |  |
| as-slice | 0.2.1 | rav1e | MIT OR Apache-2.0 | https://github.com/japaric/as-slice |  |
| av-data | 0.4.4 | librsvg | MIT |  |  |
| av-metrics | 0.9.1 | rav1e | MIT | https://github.com/rust-av/av-metrics | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e); sin archivo de licencia en el tarball |
| av-scenechange | 0.14.1 | rav1e | MIT | https://github.com/rust-av/av-scenechange |  |
| av1-grain | 0.2.4 | rav1e | BSD-2-Clause | https://github.com/rust-av/av1-grain |  |
| bitflags | 2.13.2 | librsvg | MIT OR Apache-2.0 | https://github.com/bitflags/bitflags |  |
| bitreader | 0.3.11 | librsvg | MIT OR Apache-2.0 | https://github.com/irauta/bitreader |  |
| bitstream-io | 4.2.0 | rav1e | MIT/Apache-2.0 | https://github.com/tuffy/bitstream-io |  |
| byte-slice-cast | 1.2.3 | librsvg | MIT | https://github.com/sdroege/bytes-num-slice-cast |  |
| bytemuck | 1.25.2 | librsvg | Zlib OR Apache-2.0 OR MIT | https://github.com/Lokathor/bytemuck |  |
| byteorder | 1.5.0 | librsvg | Unlicense OR MIT | https://github.com/BurntSushi/byteorder |  |
| byteorder-lite | 0.1.0 | librsvg | Unlicense OR MIT | https://github.com/image-rs/byteorder-lite |  |
| bytes | 1.12.1 | librsvg | MIT | https://github.com/tokio-rs/bytes |  |
| cairo-rs | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| cairo-sys-rs | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| cast | 0.3.0 | librsvg | MIT OR Apache-2.0 | https://github.com/japaric/cast.rs |  |
| cfg-if | 1.0.1 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-lang/cfg-if |  |
| cfg-if | 1.0.5 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/cfg-if |  |
| clap | 4.5.40 | rav1e | MIT OR Apache-2.0 | https://github.com/clap-rs/clap | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| clap_builder | 4.5.40 | rav1e | MIT OR Apache-2.0 | https://github.com/clap-rs/clap | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| clap_complete | 4.5.54 | rav1e | MIT OR Apache-2.0 | https://github.com/clap-rs/clap | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| clap_derive | 4.5.40 | rav1e | MIT OR Apache-2.0 | https://github.com/clap-rs/clap | proc-macro; posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| clap_lex | 0.7.5 | rav1e | MIT OR Apache-2.0 | https://github.com/clap-rs/clap | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| color_quant | 1.1.0 | librsvg | MIT | https://github.com/image-rs/color_quant.git |  |
| colorchoice | 1.0.4 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-cli/anstyle.git | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| console | 0.15.11 | rav1e | MIT | https://github.com/console-rs/console | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| core2 | 0.4.0 | rav1e | Apache-2.0 OR MIT | https://github.com/bbqsrc/core2 |  |
| core_detect | 1.0.0 | librsvg | MIT/Apache-2.0 | https://github.com/thomcc/core_detect |  |
| crc32fast | 1.5.2 | librsvg | MIT OR Apache-2.0 | https://github.com/srijs/rust-crc32fast |  |
| crossbeam | 0.8.4 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| crossbeam-channel | 0.5.15 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| crossbeam-deque | 0.8.6 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| crossbeam-deque | 0.8.8 | librsvg | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| crossbeam-epoch | 0.9.18 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| crossbeam-epoch | 0.9.21 | librsvg | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| crossbeam-queue | 0.3.12 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| crossbeam-utils | 0.8.21 | rav1e | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| crossbeam-utils | 0.8.23 | librsvg | MIT OR Apache-2.0 | https://github.com/crossbeam-rs/crossbeam |  |
| cssparser | 0.37.0 | librsvg | MPL-2.0 | https://github.com/servo/rust-cssparser |  |
| cssparser-color | 0.5.0 | librsvg | MPL-2.0 | https://github.com/servo/rust-cssparser |  |
| cssparser-macros | 0.7.1 | librsvg | MPL-2.0 | https://github.com/servo/rust-cssparser | proc-macro |
| data-url | 0.3.2 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-url |  |
| dav1d | 0.11.1 | librsvg | MIT | https://github.com/rust-av/dav1d-rs |  |
| dav1d-sys | 0.8.3 | librsvg | MIT | https://github.com/rust-av/dav1d-rs |  |
| derive_more | 2.1.1 | librsvg | MIT | https://github.com/JelteF/derive_more |  |
| derive_more-impl | 2.1.1 | librsvg | MIT | https://github.com/JelteF/derive_more | proc-macro |
| displaydoc | 0.2.7 | librsvg | MIT OR Apache-2.0 | https://github.com/yaahc/displaydoc | proc-macro |
| dtoa | 1.0.11 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/dtoa |  |
| dtoa-short | 0.3.5 | librsvg | MPL-2.0 | https://github.com/upsuper/dtoa-short |  |
| either | 1.15.0 | rav1e | MIT OR Apache-2.0 | https://github.com/rayon-rs/either |  |
| either | 1.18.0 | librsvg | MIT OR Apache-2.0 | https://github.com/rayon-rs/either |  |
| encode_unicode | 1.0.0 | rav1e | Apache-2.0 OR MIT | https://github.com/tormol/encode_unicode | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| encoding_rs | 0.8.41 | librsvg | (Apache-2.0 OR MIT) AND BSD-3-Clause | https://github.com/hsivonen/encoding_rs |  |
| equator | 0.4.2 | rav1e | MIT | https://github.com/sarah-ek/equator/ |  |
| equator-macro | 0.4.2 | rav1e | MIT | https://github.com/sarah-ek/equator/ | proc-macro |
| fallible_collections | 0.4.9 | librsvg | MIT/Apache-2.0 | https://github.com/vcombey/fallible_collections.git |  |
| fastrand | 2.5.0 | librsvg | Apache-2.0 OR MIT | https://github.com/smol-rs/fastrand | solo en tiempo de compilación (dependencia de un proc-macro) |
| fdeflate | 0.3.7 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/fdeflate |  |
| fern | 0.7.1 | rav1e | MIT | https://github.com/daboross/fern | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| flate2 | 1.1.10 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/flate2-rs |  |
| float-cmp | 0.10.0 | librsvg | MIT | https://github.com/mikedilger/float-cmp |  |
| form_urlencoded | 1.2.2 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-url |  |
| futures-channel | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| futures-core | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| futures-executor | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| futures-io | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| futures-macro | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs | proc-macro |
| futures-task | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| futures-util | 0.3.34 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/futures-rs |  |
| gif | 0.14.2 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/image-gif |  |
| gio | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| gio-sys | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| glib | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| glib-macros | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core | proc-macro |
| glib-sys | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| gobject-sys | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| hashbrown | 0.13.2 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/hashbrown |  |
| heck | 0.5.0 | ambas | MIT OR Apache-2.0 | https://github.com/withoutboats/heck | solo en tiempo de compilación (dependencia de un proc-macro) |
| icu_collections | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_locale_core | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_normalizer | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_normalizer_data | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_properties | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_properties_data | 2.3.0 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| icu_provider | 2.3.1 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| idna | 1.1.0 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-url/ |  |
| idna_adapter | 1.2.2 | librsvg | Apache-2.0 OR MIT | https://github.com/hsivonen/idna_adapter |  |
| image | 0.25.10 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/image |  |
| image-webp | 0.2.4 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/image-webp |  |
| is_terminal_polyfill | 1.70.1 | rav1e | MIT OR Apache-2.0 | https://github.com/polyfill-rs/is_terminal_polyfill | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| itertools | 0.10.5 | rav1e | MIT/Apache-2.0 | https://github.com/rust-itertools/itertools | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| itertools | 0.14.0 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-itertools/itertools |  |
| itertools | 0.15.0 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-itertools/itertools |  |
| itoa | 1.0.18 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/itoa |  |
| ivf | 0.1.4 | rav1e | BSD-2-Clause | https://github.com/xiph/rav1e | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e); crate del propio repositorio de rav1e (carpeta ivf/), no de crates.io |
| lab | 0.11.0 | rav1e | MIT | https://github.com/TooManyBees/lab | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| language-tags | 0.3.2 | librsvg | MIT/Apache-2.0 | https://github.com/pyfisch/rust-language-tags |  |
| lazy_static | 1.5.0 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang-nursery/lazy-static.rs |  |
| libc | 0.2.172 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-lang/libc |  |
| libc | 0.2.189 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/libc |  |
| litemap | 0.8.3 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| locale_config | 0.3.0 | librsvg | MIT | https://github.com/rust-locale/locale_config/ |  |
| lock_api | 0.4.14 | librsvg | MIT OR Apache-2.0 | https://github.com/Amanieu/parking_lot |  |
| log | 0.4.34 | ambas | MIT OR Apache-2.0 | https://github.com/rust-lang/log |  |
| markup5ever | 0.39.0 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/html5ever |  |
| matrixmultiply | 0.3.11 | librsvg | MIT/Apache-2.0 | https://github.com/bluss/matrixmultiply/ |  |
| maybe-rayon | 0.1.1 | rav1e | MIT | https://github.com/shssoichiro/maybe-rayon |  |
| memchr | 2.7.5 | rav1e | Unlicense OR MIT | https://github.com/BurntSushi/memchr |  |
| memchr | 2.8.3 | librsvg | Unlicense OR MIT | https://github.com/BurntSushi/memchr |  |
| minimal-lexical | 0.2.1 | rav1e | MIT/Apache-2.0 | https://github.com/Alexhuszagh/minimal-lexical |  |
| miniz_oxide | 0.8.9 | librsvg | MIT OR Zlib OR Apache-2.0 | https://github.com/Frommi/miniz_oxide/tree/master/miniz_oxide |  |
| miniz_oxide | 0.9.1 | librsvg | MIT OR Zlib OR Apache-2.0 | https://github.com/Frommi/miniz_oxide/tree/master/miniz_oxide |  |
| moxcms | 0.8.1 | librsvg | BSD-3-Clause OR Apache-2.0 | https://github.com/awxkee/moxcms.git |  |
| mp4parse | 0.17.0 | librsvg | MPL-2.0 | https://github.com/mozilla/mp4parse-rust |  |
| multiversion | 0.9.0 | librsvg | MIT OR Apache-2.0 | https://github.com/calebzulawski/multiversion |  |
| multiversion-macros | 0.9.0 | librsvg | MIT OR Apache-2.0 | https://github.com/calebzulawski/multiversion | proc-macro |
| multiversion_no_op | 1.0.0 | librsvg | Apache-2.0 OR MIT | https://github.com/hsivonen/multiversion_no_op | proc-macro |
| mutants | 0.0.4 | librsvg | MIT | https://github.com/sourcefrog/cargo-mutants | proc-macro; sin archivo de licencia en el tarball |
| nalgebra | 0.35.0 | librsvg | Apache-2.0 | https://github.com/dimforge/nalgebra |  |
| nalgebra-macros | 0.3.0 | librsvg | Apache-2.0 | https://github.com/dimforge/nalgebra | proc-macro |
| new_debug_unreachable | 1.0.6 | ambas | MIT | https://github.com/mbrubeck/rust-debug-unreachable |  |
| nom | 7.1.3 | rav1e | MIT | https://github.com/Geal/nom |  |
| nom | 8.0.0 | rav1e | MIT | https://github.com/rust-bakery/nom | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| noop_proc_macro | 0.3.0 | rav1e | MIT | https://github.com/lu-zero/noop_proc_macro | proc-macro |
| num-bigint | 0.4.6 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-num/num-bigint |  |
| num-bigint | 0.4.8 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-num/num-bigint |  |
| num-complex | 0.4.6 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-num/num-complex |  |
| num-derive | 0.4.2 | ambas | MIT OR Apache-2.0 | https://github.com/rust-num/num-derive | proc-macro |
| num-integer | 0.1.46 | rav1e | MIT OR Apache-2.0 | https://github.com/rust-num/num-integer |  |
| num-integer | 0.1.47 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-num/num-integer |  |
| num-rational | 0.4.2 | ambas | MIT OR Apache-2.0 | https://github.com/rust-num/num-rational |  |
| num-traits | 0.2.19 | ambas | MIT OR Apache-2.0 | https://github.com/rust-num/num-traits |  |
| once_cell | 1.21.3 | rav1e | MIT OR Apache-2.0 | https://github.com/matklad/once_cell | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| once_cell | 1.21.4 | librsvg | MIT OR Apache-2.0 | https://github.com/matklad/once_cell |  |
| once_cell_polyfill | 1.70.1 | rav1e | MIT OR Apache-2.0 | https://github.com/polyfill-rs/once_cell_polyfill | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| pango | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| pango-sys | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| pangocairo | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| pangocairo-sys | 0.22.9 | librsvg | MIT | https://github.com/gtk-rs/gtk-rs-core |  |
| parking_lot | 0.12.5 | librsvg | MIT OR Apache-2.0 | https://github.com/Amanieu/parking_lot |  |
| parking_lot_core | 0.9.12 | librsvg | MIT OR Apache-2.0 | https://github.com/Amanieu/parking_lot |  |
| pastey | 0.1.0 | rav1e | MIT OR Apache-2.0 | https://github.com/as1100k/pastey | proc-macro |
| percent-encoding | 2.3.2 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-url/ |  |
| phf | 0.13.1 | librsvg | MIT | https://github.com/rust-phf/rust-phf |  |
| phf_generator | 0.13.1 | librsvg | MIT | https://github.com/rust-phf/rust-phf | solo en tiempo de compilación (dependencia de un proc-macro) |
| phf_macros | 0.13.1 | librsvg | MIT | https://github.com/rust-phf/rust-phf | proc-macro |
| phf_shared | 0.13.1 | librsvg | MIT | https://github.com/rust-phf/rust-phf |  |
| phf_shared | 0.14.0 | librsvg | MIT | https://github.com/rust-phf/rust-phf |  |
| pin-project-lite | 0.2.17 | librsvg | Apache-2.0 OR MIT | https://github.com/taiki-e/pin-project-lite |  |
| png | 0.18.1 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/image-png |  |
| potential_utf | 0.1.6 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| precomputed-hash | 0.1.1 | librsvg | MIT | https://github.com/emilio/precomputed-hash |  |
| proc-macro2 | 1.0.107 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/proc-macro2 | solo en tiempo de compilación (dependencia de un proc-macro) |
| proc-macro2 | 1.0.95 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/proc-macro2 | solo en tiempo de compilación (dependencia de un proc-macro) |
| profiling | 1.0.16 | rav1e | MIT OR Apache-2.0 | https://github.com/aclysma/profiling | sin archivo de licencia en el tarball |
| profiling-procmacros | 1.0.16 | rav1e | MIT OR Apache-2.0 | https://github.com/aclysma/profiling | proc-macro; sin archivo de licencia en el tarball |
| pxfm | 0.1.30 | librsvg | BSD-3-Clause OR Apache-2.0 | https://github.com/awxkee/pxfm |  |
| quick-error | 2.0.1 | librsvg | MIT/Apache-2.0 | http://github.com/tailhook/quick-error |  |
| quote | 1.0.40 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/quote | solo en tiempo de compilación (dependencia de un proc-macro) |
| quote | 1.0.47 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/quote | solo en tiempo de compilación (dependencia de un proc-macro) |
| rawpointer | 0.2.1 | librsvg | MIT/Apache-2.0 | https://github.com/bluss/rawpointer/ |  |
| rayon | 1.10.0 | rav1e | MIT OR Apache-2.0 | https://github.com/rayon-rs/rayon |  |
| rayon | 1.12.0 | librsvg | MIT OR Apache-2.0 | https://github.com/rayon-rs/rayon |  |
| rayon-core | 1.12.1 | rav1e | MIT OR Apache-2.0 | https://github.com/rayon-rs/rayon |  |
| rayon-core | 1.13.0 | librsvg | MIT OR Apache-2.0 | https://github.com/rayon-rs/rayon |  |
| rctree | 0.6.0 | librsvg | MIT | https://github.com/RazrFalcon/rctree |  |
| regex | 1.13.1 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/regex |  |
| regex-automata | 0.4.18 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/regex |  |
| regex-syntax | 0.8.11 | librsvg | MIT OR Apache-2.0 | https://github.com/rust-lang/regex |  |
| rgb | 0.8.53 | librsvg | MIT | https://github.com/kornelski/rust-rgb |  |
| rustc-hash | 2.1.3 | librsvg | Apache-2.0 OR MIT | https://github.com/rust-lang/rustc-hash |  |
| rustversion | 1.0.23 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/rustversion | proc-macro |
| safe_arch | 1.2.0 | librsvg | Zlib OR Apache-2.0 OR MIT | https://github.com/Lokathor/safe_arch |  |
| scan_fmt | 0.2.6 | rav1e | MIT | https://github.com/wlentz/scan_fmt |  |
| scopeguard | 1.2.0 | librsvg | MIT OR Apache-2.0 | https://github.com/bluss/scopeguard |  |
| selectors | 0.40.0 | librsvg | MPL-2.0 | https://github.com/servo/stylo | sin archivo de licencia en el tarball |
| serde | 1.0.229 | librsvg | MIT OR Apache-2.0 | https://github.com/serde-rs/serde |  |
| serde_core | 1.0.229 | librsvg | MIT OR Apache-2.0 | https://github.com/serde-rs/serde |  |
| servo_arc | 0.4.3 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/stylo |  |
| simba | 0.10.2 | librsvg | Apache-2.0 | https://github.com/dimforge/simba |  |
| simd-adler32 | 0.3.10 | librsvg | MIT | https://github.com/mcountryman/simd-adler32 |  |
| simd_helpers | 0.1.0 | rav1e | MIT | https://github.com/lu-zero/simd_helpers | proc-macro; sin archivo de licencia en el tarball |
| simdutf8 | 0.1.5 | librsvg | MIT OR Apache-2.0 | https://github.com/rusticstuff/simdutf8 |  |
| siphasher | 1.0.3 | librsvg | MIT/Apache-2.0 | https://github.com/jedisct1/rust-siphash |  |
| slab | 0.4.12 | librsvg | MIT | https://github.com/tokio-rs/slab |  |
| smallvec | 1.16.1 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-smallvec |  |
| stable_deref_trait | 1.2.0 | rav1e | MIT/Apache-2.0 | https://github.com/storyyeller/stable_deref_trait |  |
| stable_deref_trait | 1.2.1 | librsvg | MIT OR Apache-2.0 | https://github.com/storyyeller/stable_deref_trait |  |
| static_assertions | 1.1.0 | librsvg | MIT OR Apache-2.0 | https://github.com/nvzqz/static-assertions-rs |  |
| string_cache | 0.10.0 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/string-cache |  |
| string_cache | 0.9.0 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/string-cache |  |
| syn | 2.0.102 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/syn | solo en tiempo de compilación (dependencia de un proc-macro) |
| syn | 2.0.119 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/syn | solo en tiempo de compilación (dependencia de un proc-macro) |
| syn | 3.0.6 | librsvg | MIT OR Apache-2.0 | https://github.com/dtolnay/syn | solo en tiempo de compilación (dependencia de un proc-macro) |
| synstructure | 0.14.0 | librsvg | MIT | https://github.com/mystor/synstructure | solo en tiempo de compilación (dependencia de un proc-macro) |
| tendril | 0.5.1 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/html5ever |  |
| terminal_size | 0.4.2 | rav1e | MIT OR Apache-2.0 | https://github.com/eminence/terminal-size | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| thiserror | 1.0.69 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/thiserror | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| thiserror | 2.0.12 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/thiserror |  |
| thiserror-impl | 1.0.69 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/thiserror | proc-macro; posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| thiserror-impl | 2.0.12 | rav1e | MIT OR Apache-2.0 | https://github.com/dtolnay/thiserror | proc-macro |
| tinystr | 0.8.4 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| tinyvec | 1.13.3 | librsvg | Zlib OR Apache-2.0 OR MIT | https://github.com/Lokathor/tinyvec |  |
| typenum | 1.20.1 | librsvg | MIT OR Apache-2.0 | https://github.com/paholg/typenum |  |
| unicode-ident | 1.0.18 | rav1e | (MIT OR Apache-2.0) AND Unicode-3.0 | https://github.com/dtolnay/unicode-ident | solo en tiempo de compilación (dependencia de un proc-macro) |
| unicode-ident | 1.0.26 | librsvg | (MIT OR Apache-2.0) AND Unicode-3.0 | https://github.com/dtolnay/unicode-ident | solo en tiempo de compilación (dependencia de un proc-macro) |
| unicode-width | 0.2.1 | rav1e | MIT OR Apache-2.0 | https://github.com/unicode-rs/unicode-width | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| url | 2.5.8 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/rust-url |  |
| utf8_iter | 1.0.4 | librsvg | Apache-2.0 OR MIT | https://github.com/hsivonen/utf8_iter |  |
| utf8parse | 0.2.2 | rav1e | Apache-2.0 OR MIT | https://github.com/alacritty/vte | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| v_frame | 0.3.9 | rav1e | BSD-2-Clause | https://github.com/rust-av/v_frame |  |
| web_atoms | 0.2.6 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/html5ever |  |
| weezl | 0.1.12 | librsvg | MIT OR Apache-2.0 | https://github.com/image-rs/weezl |  |
| wide | 1.7.1 | librsvg | Zlib OR Apache-2.0 OR MIT | https://github.com/Lokathor/wide |  |
| winapi | 0.3.9 | librsvg | MIT/Apache-2.0 | https://github.com/retep998/winapi-rs |  |
| winapi-x86_64-pc-windows-gnu | 0.4.0 | librsvg | MIT/Apache-2.0 | https://github.com/retep998/winapi-rs | sin archivo de licencia en el tarball |
| windows-link | 0.2.1 | librsvg | MIT OR Apache-2.0 | https://github.com/microsoft/windows-rs |  |
| windows-sys | 0.59.0 | rav1e | MIT OR Apache-2.0 | https://github.com/microsoft/windows-rs | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| windows-sys | 0.61.2 | librsvg | MIT OR Apache-2.0 | https://github.com/microsoft/windows-rs |  |
| windows-targets | 0.52.6 | rav1e | MIT OR Apache-2.0 | https://github.com/microsoft/windows-rs | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| windows_x86_64_gnu | 0.52.6 | rav1e | MIT OR Apache-2.0 | https://github.com/microsoft/windows-rs | posible: lo activa la feature por defecto «binaries» de rav1e, pero el código de la biblioteca no lo usa (solo el ejecutable rav1e) |
| writeable | 0.6.4 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| xml5ever | 0.39.0 | librsvg | MIT OR Apache-2.0 | https://github.com/servo/html5ever |  |
| y4m | 0.8.0 | rav1e | MIT | https://github.com/image-rs/y4m.git |  |
| yoke | 0.8.3 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| yoke-derive | 0.8.3 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x | proc-macro |
| zerocopy | 0.8.57 | librsvg | BSD-2-Clause OR Apache-2.0 OR MIT | https://github.com/google/zerocopy |  |
| zerofrom | 0.1.8 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| zerofrom-derive | 0.1.8 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x | proc-macro |
| zerotrie | 0.2.5 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| zerovec | 0.11.8 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x |  |
| zerovec-derive | 0.11.6 | librsvg | Unicode-3.0 | https://github.com/unicode-org/icu4x | proc-macro |
| zune-core | 0.5.3 | librsvg | MIT OR Apache-2.0 OR Zlib | https://github.com/etemesi254/zune-image |  |
| zune-jpeg | 0.5.15 | librsvg | MIT OR Apache-2.0 OR Zlib | https://github.com/etemesi254/zune-image/tree/dev/crates/zune-jpeg |  |

## Resumen

- Crates en total: 248 (rav1e: 81, librsvg: 173, en ambas: 6).
- Dentro del binario (sin proc-macro ni sus dependencias exclusivas): 211; proc-macro: 24; solo en tiempo de compilación por ser dependencia de proc-macro: 13.
- Marcados «posible» (feature `binaries` de rav1e, no usados por la biblioteca): 33.
- Sin archivo de licencia en el tarball: 4 (av-metrics 0.9.1, profiling 1.0.16, selectors 0.40.0, winapi-x86_64-pc-windows-gnu 0.4.0).
- Licencias distintas (expresiones SPDX): 17.

| Expresión SPDX | Crates |
|---|---|
| MIT OR Apache-2.0 | 124: ahash 0.8.12, aligned 0.4.2, anstream 0.6.19, anstyle 1.0.11, anstyle-parse 0.2.7, anstyle-query 1.1.3, anstyle-wincon 3.0.9, anyhow 1.0.98, arrayvec 0.7.6, as-slice 0.2.1, bitflags 2.13.2, bitreader 0.3.11, cast 0.3.0, cfg-if 1.0.1, cfg-if 1.0.5, clap 4.5.40, clap_builder 4.5.40, clap_complete 4.5.54, clap_derive 4.5.40, clap_lex 0.7.5, colorchoice 1.0.4, crc32fast 1.5.2, crossbeam 0.8.4, crossbeam-channel 0.5.15, crossbeam-deque 0.8.6, crossbeam-deque 0.8.8, crossbeam-epoch 0.9.18, crossbeam-epoch 0.9.21, crossbeam-queue 0.3.12, crossbeam-utils 0.8.21, crossbeam-utils 0.8.23, data-url 0.3.2, displaydoc 0.2.7, dtoa 1.0.11, either 1.15.0, either 1.18.0, fdeflate 0.3.7, flate2 1.1.10, form_urlencoded 1.2.2, futures-channel 0.3.34, futures-core 0.3.34, futures-executor 0.3.34, futures-io 0.3.34, futures-macro 0.3.34, futures-task 0.3.34, futures-util 0.3.34, gif 0.14.2, hashbrown 0.13.2, heck 0.5.0, idna 1.1.0, image 0.25.10, image-webp 0.2.4, is_terminal_polyfill 1.70.1, itertools 0.14.0, itertools 0.15.0, itoa 1.0.18, lazy_static 1.5.0, libc 0.2.172, libc 0.2.189, lock_api 0.4.14, log 0.4.34, markup5ever 0.39.0, multiversion 0.9.0, multiversion-macros 0.9.0, num-bigint 0.4.6, num-bigint 0.4.8, num-complex 0.4.6, num-derive 0.4.2, num-integer 0.1.46, num-integer 0.1.47, num-rational 0.4.2, num-traits 0.2.19, once_cell 1.21.3, once_cell 1.21.4, once_cell_polyfill 1.70.1, parking_lot 0.12.5, parking_lot_core 0.9.12, pastey 0.1.0, percent-encoding 2.3.2, png 0.18.1, proc-macro2 1.0.107, proc-macro2 1.0.95, profiling 1.0.16, profiling-procmacros 1.0.16, quote 1.0.40, quote 1.0.47, rayon 1.10.0, rayon 1.12.0, rayon-core 1.12.1, rayon-core 1.13.0, regex 1.13.1, regex-automata 0.4.18, regex-syntax 0.8.11, rustversion 1.0.23, scopeguard 1.2.0, serde 1.0.229, serde_core 1.0.229, servo_arc 0.4.3, simdutf8 0.1.5, smallvec 1.16.1, stable_deref_trait 1.2.1, static_assertions 1.1.0, string_cache 0.10.0, string_cache 0.9.0, syn 2.0.102, syn 2.0.119, syn 3.0.6, tendril 0.5.1, terminal_size 0.4.2, thiserror 1.0.69, thiserror 2.0.12, thiserror-impl 1.0.69, thiserror-impl 2.0.12, typenum 1.20.1, unicode-width 0.2.1, url 2.5.8, web_atoms 0.2.6, weezl 0.1.12, windows-link 0.2.1, windows-sys 0.59.0, windows-sys 0.61.2, windows-targets 0.52.6, windows_x86_64_gnu 0.52.6, xml5ever 0.39.0 |
| MIT | 51: aligned-vec 0.6.4, arg_enum_proc_macro 0.3.4, av-data 0.4.4, av-metrics 0.9.1, av-scenechange 0.14.1, byte-slice-cast 1.2.3, bytes 1.12.1, cairo-rs 0.22.9, cairo-sys-rs 0.22.9, color_quant 1.1.0, console 0.15.11, dav1d 0.11.1, dav1d-sys 0.8.3, derive_more 2.1.1, derive_more-impl 2.1.1, equator 0.4.2, equator-macro 0.4.2, fern 0.7.1, float-cmp 0.10.0, gio 0.22.9, gio-sys 0.22.9, glib 0.22.9, glib-macros 0.22.9, glib-sys 0.22.9, gobject-sys 0.22.9, lab 0.11.0, locale_config 0.3.0, maybe-rayon 0.1.1, mutants 0.0.4, new_debug_unreachable 1.0.6, nom 7.1.3, nom 8.0.0, noop_proc_macro 0.3.0, pango 0.22.9, pango-sys 0.22.9, pangocairo 0.22.9, pangocairo-sys 0.22.9, phf 0.13.1, phf_generator 0.13.1, phf_macros 0.13.1, phf_shared 0.13.1, phf_shared 0.14.0, precomputed-hash 0.1.1, rctree 0.6.0, rgb 0.8.53, scan_fmt 0.2.6, simd-adler32 0.3.10, simd_helpers 0.1.0, slab 0.4.12, synstructure 0.14.0, y4m 0.8.0 |
| Unicode-3.0 | 18: icu_collections 2.3.0, icu_locale_core 2.3.0, icu_normalizer 2.3.0, icu_normalizer_data 2.3.0, icu_properties 2.3.0, icu_properties_data 2.3.0, icu_provider 2.3.1, litemap 0.8.3, potential_utf 0.1.6, tinystr 0.8.4, writeable 0.6.4, yoke 0.8.3, yoke-derive 0.8.3, zerofrom 0.1.8, zerofrom-derive 0.1.8, zerotrie 0.2.5, zerovec 0.11.8, zerovec-derive 0.11.6 |
| MIT/Apache-2.0 | 13: bitstream-io 4.2.0, core_detect 1.0.0, fallible_collections 0.4.9, itertools 0.10.5, language-tags 0.3.2, matrixmultiply 0.3.11, minimal-lexical 0.2.1, quick-error 2.0.1, rawpointer 0.2.1, siphasher 1.0.3, stable_deref_trait 1.2.0, winapi 0.3.9, winapi-x86_64-pc-windows-gnu 0.4.0 |
| Apache-2.0 OR MIT | 9: core2 0.4.0, encode_unicode 1.0.0, fastrand 2.5.0, idna_adapter 1.2.2, multiversion_no_op 1.0.0, pin-project-lite 0.2.17, rustc-hash 2.1.3, utf8_iter 1.0.4, utf8parse 0.2.2 |
| MPL-2.0 | 6: cssparser 0.37.0, cssparser-color 0.5.0, cssparser-macros 0.7.1, dtoa-short 0.3.5, mp4parse 0.17.0, selectors 0.40.0 |
| Unlicense OR MIT | 5: aho-corasick 1.1.5, byteorder 1.5.0, byteorder-lite 0.1.0, memchr 2.7.5, memchr 2.8.3 |
| Apache-2.0 | 4: approx 0.5.1, nalgebra 0.35.0, nalgebra-macros 0.3.0, simba 0.10.2 |
| Zlib OR Apache-2.0 OR MIT | 4: bytemuck 1.25.2, safe_arch 1.2.0, tinyvec 1.13.3, wide 1.7.1 |
| BSD-2-Clause | 3: av1-grain 0.2.4, ivf 0.1.4, v_frame 0.3.9 |
| (MIT OR Apache-2.0) AND Unicode-3.0 | 2: unicode-ident 1.0.18, unicode-ident 1.0.26 |
| BSD-3-Clause OR Apache-2.0 | 2: moxcms 0.8.1, pxfm 0.1.30 |
| MIT OR Apache-2.0 OR Zlib | 2: zune-core 0.5.3, zune-jpeg 0.5.15 |
| MIT OR Zlib OR Apache-2.0 | 2: miniz_oxide 0.8.9, miniz_oxide 0.9.1 |
| (Apache-2.0 OR MIT) AND BSD-3-Clause | 1: encoding_rs 0.8.41 |
| 0BSD OR MIT OR Apache-2.0 | 1: adler2 2.0.1 |
| BSD-2-Clause OR Apache-2.0 OR MIT | 1: zerocopy 0.8.57 |

### Licencias fuera de la lista habitual (MIT, Apache-2.0, BSD, ISC, Zlib, Unicode, MPL-2.0, LGPL, 0BSD)

- **Unlicense**: aho-corasick 1.1.5 (Unlicense OR MIT); byteorder 1.5.0 (Unlicense OR MIT); byteorder-lite 0.1.0 (Unlicense OR MIT); memchr 2.7.5 (Unlicense OR MIT); memchr 2.8.3 (Unlicense OR MIT)

### Crates sin ninguna alternativa en la lista admitida

- Ninguno.

### Predicados cfg no reconocidos (se evaluaron como falsos)

`crossbeam_loom`, `windows_raw_dylib`

