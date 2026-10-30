//! Pins the exact Seiza C ABI revision used by the Windows application.
//!
//! The application build locates this resolved dependency with `cargo metadata`
//! and builds the upstream `seiza-cabi` package directly. No ABI implementation
//! is maintained in this repository.

pub use seiza_cabi as upstream;
