fn main() {
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        // rustc emits the COM entry points through a module-definition file.
        // MSVC's recommendation to mark them PRIVATE is inapplicable: COM
        // activation requires both names to remain public DLL exports.
        println!("cargo:rustc-link-arg=/IGNORE:4104");
    }
}
