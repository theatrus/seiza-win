use quick_xml::Reader;
use quick_xml::events::{BytesStart, Event};
use std::collections::{BTreeMap, HashSet};

const XISF_PREAMBLE_BYTES: usize = 16;
const MAX_HEADER_BYTES: usize = 16 * 1024 * 1024;
const MAX_ESTIMATED_WORKING_BYTES: u64 = 1536 * 1024 * 1024;
const MAX_THUMBNAIL_BYTES: u64 = 4096 * 4096 * 4;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ImageLayout {
    width: u64,
    height: u64,
    planes: u64,
    bytes_per_sample: u64,
    compressed: bool,
    bayer: bool,
}

pub(crate) fn validate_image_budget(bytes: &[u8]) -> Result<(), String> {
    let layout = if bytes.starts_with(b"XISF0100") {
        inspect_xisf(bytes)?
    } else {
        inspect_fits(bytes)?
    };

    let pixels = checked_mul(layout.width, layout.height)?;
    let samples = checked_mul(pixels, layout.planes)?;
    let decoded_bytes = checked_mul(samples, layout.bytes_per_sample)?;

    // XISF's compressed decoders temporarily retain the uncompressed byte
    // block while converting it into the FitsImage pixel representation.
    let decompression_bytes = if layout.compressed { decoded_bytes } else { 0 };

    // The current renderer retains interleaved u16 RGB, per-channel u16 data,
    // and stretched u8 channels together at its peak. Monochrome conversion
    // needs at most a u16 normalization buffer and one stretched u8 buffer.
    let render_bytes_per_pixel = if layout.planes == 3 || layout.bayer {
        15
    } else {
        3
    };
    let render_bytes = checked_mul(pixels, render_bytes_per_pixel)?;

    let estimated = [
        bytes.len() as u64,
        decoded_bytes,
        decompression_bytes,
        render_bytes,
        MAX_THUMBNAIL_BYTES,
    ]
    .into_iter()
    .try_fold(0u64, |total, value| {
        total
            .checked_add(value)
            .ok_or_else(|| "thumbnail working-set estimate overflowed".to_string())
    })?;

    if estimated > MAX_ESTIMATED_WORKING_BYTES {
        return Err(format!(
            "estimated thumbnail working set is {} MiB; limit is {} MiB",
            estimated.div_ceil(1024 * 1024),
            MAX_ESTIMATED_WORKING_BYTES / (1024 * 1024)
        ));
    }
    Ok(())
}

fn inspect_fits(bytes: &[u8]) -> Result<ImageLayout, String> {
    if bytes.len() < 2880 || !bytes.starts_with(b"SIMPLE") {
        return Err("not a FITS image".into());
    }

    let mut bitpix = None;
    let mut naxis = None;
    let mut dimensions = BTreeMap::new();
    let mut structural_keywords = HashSet::new();
    let mut bayer = false;
    let header_limit = bytes.len().min(MAX_HEADER_BYTES);
    let mut found_end = false;

    for card in bytes[..header_limit].as_chunks::<80>().0 {
        // The decoder trims the end of ordinary keywords, not the start.
        let mut keyword = std::str::from_utf8(&card[..8])
            .map_err(|_| "FITS header keyword is not UTF-8")?
            .trim_end()
            .as_bytes();
        if keyword == b"END" {
            found_end = true;
            break;
        }
        let raw_value = if keyword == b"HIERARCH" && card[8] != b'=' {
            // seiza-fits 0.3 also accepts HIERARCH cards, including names
            // that affect the image budget. Inspect the same name and value
            // so an extended card cannot hide an empty or Bayer primary.
            let extended = &card[8..];
            let Some(equals) = extended.iter().position(|byte| *byte == b'=') else {
                continue;
            };
            keyword = std::str::from_utf8(&extended[..equals])
                .map_err(|_| "FITS HIERARCH keyword is not UTF-8")?
                .trim()
                .as_bytes();
            &extended[equals + 1..]
        } else {
            if card[8] != b'=' {
                continue;
            }
            &card[9..]
        };
        let value = trim_ascii(
            raw_value
                .split(|byte| *byte == b'/')
                .next()
                .unwrap_or_default(),
        );
        let axis = keyword.strip_prefix(b"NAXIS").and_then(|suffix| {
            // Match the decoder's canonical NAXIS1, NAXIS2, ... keys. A
            // noncanonical NAXIS01 must not replace NAXIS1 in this estimate.
            if suffix.first() == Some(&b'0') || !suffix.iter().all(u8::is_ascii_digit) {
                None
            } else {
                parse_i64(suffix)
            }
        });
        if (matches!(keyword, b"BITPIX" | b"NAXIS" | b"BAYERPAT") || axis.is_some())
            && !structural_keywords.insert(keyword)
        {
            return Err("FITS header has duplicate structural cards".into());
        }
        match keyword {
            b"BITPIX" => bitpix = parse_i64(value),
            b"NAXIS" => naxis = parse_i64(value),
            b"BAYERPAT" => bayer = !value.is_empty(),
            _ => {
                if let Some(axis) = axis {
                    dimensions.insert(axis, parse_positive_u64(value));
                }
            }
        }
    }

    if !found_end {
        return Err("FITS header is missing END or exceeds 16 MiB".into());
    }
    let bitpix = bitpix.ok_or("FITS header is missing BITPIX")?;
    let bytes_per_sample = match bitpix {
        8 => 1,
        // seiza-fits 0.3 promotes signed or scaled 16-bit samples to f32.
        // The header alone cannot prove that all unscaled samples are positive.
        16 => 4,
        32 | -32 => 4,
        -64 => 8,
        _ => return Err(format!("unsupported FITS BITPIX {bitpix}")),
    };
    let naxis = naxis.ok_or("FITS header is missing NAXIS")?;
    if !(2..=999).contains(&naxis) {
        return Err(format!("unsupported FITS primary NAXIS {naxis}"));
    }
    // seiza-fits 0.3 follows an empty primary HDU to an image extension,
    // including tile-compressed extensions. Until preflight inspects that
    // selected extension, reject every empty or incomplete primary shape.
    for axis in 1..=naxis {
        if !dimensions.get(&axis).is_some_and(Option::is_some) {
            return Err(format!(
                "FITS primary NAXIS{axis} is missing or not positive"
            ));
        }
    }
    let planes = if naxis >= 3 {
        dimensions[&3].unwrap().clamp(1, 3)
    } else {
        1
    };

    Ok(ImageLayout {
        width: dimensions[&1].unwrap(),
        height: dimensions[&2].unwrap(),
        planes,
        bytes_per_sample,
        compressed: false,
        bayer,
    })
}

fn inspect_xisf(bytes: &[u8]) -> Result<ImageLayout, String> {
    if bytes.len() < XISF_PREAMBLE_BYTES {
        return Err("XISF preamble is truncated".into());
    }
    let header_bytes = u32::from_le_bytes(
        bytes[8..12]
            .try_into()
            .map_err(|_| "XISF header length is invalid")?,
    ) as usize;
    if header_bytes > MAX_HEADER_BYTES {
        return Err("XISF header exceeds 16 MiB".into());
    }
    let header_end = XISF_PREAMBLE_BYTES
        .checked_add(header_bytes)
        .ok_or("XISF header range overflowed")?;
    let header = bytes
        .get(XISF_PREAMBLE_BYTES..header_end)
        .ok_or("XISF header is truncated")?;

    let mut reader = Reader::from_reader(header);
    reader.config_mut().trim_text(true);
    let mut buffer = Vec::new();
    let mut layout = None;
    let mut inside_first_image = false;

    loop {
        match reader
            .read_event_into(&mut buffer)
            .map_err(|error| format!("invalid XISF XML header: {error}"))?
        {
            Event::Start(element)
                if element.local_name().as_ref() == "Image" && layout.is_none() =>
            {
                layout = Some(xisf_image_layout(&element)?);
                inside_first_image = true;
            }
            Event::Empty(element)
                if element.local_name().as_ref() == "Image" && layout.is_none() =>
            {
                layout = Some(xisf_image_layout(&element)?);
                break;
            }
            Event::Start(element) | Event::Empty(element)
                if inside_first_image && element.local_name().as_ref() == "ColorFilterArray" =>
            {
                if let Some(layout) = &mut layout {
                    layout.bayer = true;
                }
            }
            Event::End(element)
                if inside_first_image && element.local_name().as_ref() == "Image" =>
            {
                break;
            }
            Event::Eof => break,
            _ => {}
        }
        buffer.clear();
    }

    layout.ok_or_else(|| "XISF header contains no image".into())
}

fn xisf_image_layout(element: &BytesStart<'_>) -> Result<ImageLayout, String> {
    let mut geometry = None;
    let mut sample_format = None;
    let mut compressed = false;
    for attribute in element.attributes() {
        let attribute =
            attribute.map_err(|error| format!("invalid XISF image attribute: {error}"))?;
        // quick-xml validates UTF-8 while reading events and exposes names and
        // raw attribute values as strings starting with 0.42.
        let value = attribute.value.as_ref();
        match attribute.key.as_ref() {
            "geometry" => geometry = Some(value.to_string()),
            "sampleFormat" => sample_format = Some(value.to_string()),
            "compression" => compressed = true,
            _ => {}
        }
    }

    let geometry = geometry.ok_or("XISF image is missing geometry")?;
    let dimensions = geometry
        .split(':')
        .map(|value| {
            value
                .parse::<u64>()
                .map_err(|_| format!("invalid XISF geometry {geometry:?}"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    if dimensions.len() != 3 || dimensions[0] == 0 || dimensions[1] == 0 {
        return Err(format!("unsupported XISF geometry {geometry:?}"));
    }
    let planes = dimensions[2];
    if !matches!(planes, 1 | 3) {
        return Err(format!("unsupported XISF plane count {planes}"));
    }

    let sample_format = sample_format.ok_or("XISF image is missing sampleFormat")?;
    let bytes_per_sample = match sample_format.as_str() {
        "UInt8" | "Byte" => 1,
        "UInt16" | "UShort" => 2,
        "UInt32" | "UInt" | "Float32" | "Float" => 4,
        "Float64" | "Double" => 8,
        _ => return Err(format!("unsupported XISF sample format {sample_format:?}")),
    };

    Ok(ImageLayout {
        width: dimensions[0],
        height: dimensions[1],
        planes,
        bytes_per_sample,
        compressed,
        bayer: false,
    })
}

fn checked_mul(left: u64, right: u64) -> Result<u64, String> {
    left.checked_mul(right)
        .ok_or_else(|| "thumbnail working-set estimate overflowed".into())
}

fn parse_i64(value: &[u8]) -> Option<i64> {
    std::str::from_utf8(value).ok()?.trim().parse().ok()
}

fn parse_positive_u64(value: &[u8]) -> Option<u64> {
    let value = parse_i64(value)?;
    u64::try_from(value).ok().filter(|value| *value > 0)
}

fn trim_ascii(mut value: &[u8]) -> &[u8] {
    while value.first().is_some_and(u8::is_ascii_whitespace) {
        value = &value[1..];
    }
    while value.last().is_some_and(u8::is_ascii_whitespace) {
        value = &value[..value.len() - 1];
    }
    value
}

#[cfg(test)]
mod tests {
    use super::{ImageLayout, inspect_fits, inspect_xisf, validate_image_budget};

    fn fits_header(width: u64, height: u64, planes: u64, bitpix: i64, bayer: bool) -> Vec<u8> {
        fn card(keyword: &str, value: &str) -> [u8; 80] {
            let mut card = [b' '; 80];
            let text = format!("{keyword:<8}= {value:>20}");
            card[..text.len()].copy_from_slice(text.as_bytes());
            card
        }

        let mut bytes = Vec::new();
        bytes.extend_from_slice(&card("SIMPLE", "T"));
        bytes.extend_from_slice(&card("BITPIX", &bitpix.to_string()));
        bytes.extend_from_slice(&card("NAXIS", if planes == 3 { "3" } else { "2" }));
        bytes.extend_from_slice(&card("NAXIS1", &width.to_string()));
        bytes.extend_from_slice(&card("NAXIS2", &height.to_string()));
        if planes == 3 {
            bytes.extend_from_slice(&card("NAXIS3", "3"));
        }
        if bayer {
            bytes.extend_from_slice(&card("BAYERPAT", "'RGGB'"));
        }
        let mut end = [b' '; 80];
        end[..3].copy_from_slice(b"END");
        bytes.extend_from_slice(&end);
        bytes.resize(2880, b' ');
        bytes
    }

    fn xisf_header(geometry: &str, sample_format: &str, compression: &str) -> Vec<u8> {
        let compression = if compression.is_empty() {
            String::new()
        } else {
            format!(" compression=\"{compression}\"")
        };
        let header = format!(
            "<?xml version=\"1.0\"?><x:xisf xmlns:x=\"urn:test\"><x:Image geometry=\"{geometry}\" sampleFormat=\"{sample_format}\"{compression}/></x:xisf>"
        );
        xisf_xml_header(header.as_bytes())
    }

    fn xisf_xml_header(header: &[u8]) -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"XISF0100");
        bytes.extend_from_slice(&(header.len() as u32).to_le_bytes());
        bytes.extend_from_slice(&[0; 4]);
        bytes.extend_from_slice(header);
        bytes
    }

    #[test]
    fn reads_namespaced_xisf_image_attributes_as_strings() {
        assert_eq!(
            inspect_xisf(&xisf_header("1200:800:3", "Float32", "zstd:11520000"))
                .expect("read namespaced, self-closing image"),
            ImageLayout {
                width: 1200,
                height: 800,
                planes: 3,
                bytes_per_sample: 4,
                compressed: true,
                bayer: false,
            }
        );
    }

    #[test]
    fn reads_nested_bayer_metadata_and_stops_after_first_image() {
        for cfa in [
            "<x:ColorFilterArray/>",
            "<x:ColorFilterArray></x:ColorFilterArray>",
        ] {
            let header = format!(
                "<x:xisf xmlns:x=\"urn:test\"><x:Image geometry=\"1200:800:1\" sampleFormat=\"UInt16\">{cfa}</x:Image><x:Image geometry=\"50000:50000:3\" sampleFormat=\"Float64\"/></x:xisf>"
            );
            assert_eq!(
                inspect_xisf(&xisf_xml_header(header.as_bytes()))
                    .expect("read first image and its Bayer flag"),
                ImageLayout {
                    width: 1200,
                    height: 800,
                    planes: 1,
                    bytes_per_sample: 2,
                    compressed: false,
                    bayer: true,
                }
            );
        }
    }

    #[test]
    fn rejects_invalid_utf8_in_xisf_attributes() {
        let header = b"<xisf><Image geometry=\"2:2:1\" sampleFormat=\"UInt\xFF16\"/></xisf>";
        let error = inspect_xisf(&xisf_xml_header(header)).expect_err("invalid UTF-8");
        assert!(error.contains("invalid XISF XML header"), "{error}");
    }

    #[test]
    fn rejects_invalid_xisf_geometry_before_decoding() {
        let error = validate_image_budget(&xisf_header("huge:800:1", "UInt16", ""))
            .expect_err("invalid geometry");
        assert!(error.contains("invalid XISF geometry"), "{error}");
    }

    #[test]
    fn accepts_representative_large_mono_fits() {
        assert!(validate_image_budget(&fits_header(12_000, 8_000, 1, 16, false)).is_ok());
    }

    fn set_fits_card(bytes: &mut [u8], index: usize, keyword: &str, value: &str) {
        let card = &mut bytes[index * 80..(index + 1) * 80];
        card.fill(b' ');
        let text = format!("{keyword:<8}= {value:>20}");
        card[..text.len()].copy_from_slice(text.as_bytes());
    }

    #[test]
    fn rejects_empty_fits_primary_even_with_stray_positive_dimensions() {
        for naxis in [0, 1] {
            let mut bytes = fits_header(2, 2, 1, 16, false);
            set_fits_card(&mut bytes, 2, "NAXIS", &naxis.to_string());
            let error = validate_image_budget(&bytes).expect_err("unsupported primary shape");
            assert!(error.contains("primary NAXIS"), "{error}");
        }
    }

    #[test]
    fn rejects_empty_third_or_higher_fits_axis_before_following_extensions() {
        for (naxis, axis) in [(3, "NAXIS3"), (4, "NAXIS4")] {
            let mut bytes = fits_header(2, 2, 3, 16, true);
            set_fits_card(&mut bytes, 2, "NAXIS", &naxis.to_string());
            set_fits_card(&mut bytes, if naxis == 3 { 5 } else { 6 }, axis, "0");
            let error = validate_image_budget(&bytes).expect_err("empty active axis");
            assert!(
                error.contains(axis) && error.contains("not positive"),
                "{error}"
            );
        }
    }

    #[test]
    fn rejects_duplicate_structural_fits_cards_instead_of_using_the_last() {
        for (keyword, value) in [
            ("NAXIS", "0"),
            ("NAXIS1", "0"),
            ("BITPIX", "-64"),
            ("BAYERPAT", "'RGGB'"),
        ] {
            let mut bytes = fits_header(2, 2, 1, 16, true);
            // Preserve the first Bayer card and END, and use another card
            // for the duplicate just before a new END.
            set_fits_card(&mut bytes, 6, keyword, value);
            bytes[7 * 80..8 * 80].fill(b' ');
            bytes[7 * 80..7 * 80 + 3].copy_from_slice(b"END");
            let error = validate_image_budget(&bytes).expect_err("duplicate structural card");
            assert!(error.contains("duplicate structural"), "{error}");
        }
    }

    fn set_hierarch_card(bytes: &mut [u8], index: usize, keyword: &str, value: &str) {
        let card = &mut bytes[index * 80..(index + 1) * 80];
        card.fill(b' ');
        let text = format!("HIERARCH {keyword} = {value}");
        card[..text.len()].copy_from_slice(text.as_bytes());
    }

    #[test]
    fn hierarch_structural_cards_cannot_hide_an_empty_primary() {
        let mut bytes = fits_header(2, 2, 1, 16, true);
        set_hierarch_card(&mut bytes, 2, "NAXIS", "0");
        let error = inspect_fits(&bytes).expect_err("empty HIERARCH primary");
        assert!(error.contains("primary NAXIS 0"), "{error}");

        set_fits_card(&mut bytes, 5, "NAXIS", "2");
        let error = inspect_fits(&bytes).expect_err("duplicate HIERARCH primary shape");
        assert!(error.contains("duplicate structural"), "{error}");
    }

    #[test]
    fn includes_hierarch_bayer_metadata_in_the_render_budget() {
        let mut bytes = fits_header(2, 2, 1, 16, true);
        set_hierarch_card(&mut bytes, 5, "BAYERPAT", "'RGGB'");
        assert!(inspect_fits(&bytes).expect("HIERARCH Bayer pattern").bayer);
    }

    #[test]
    fn does_not_treat_leading_spaces_as_a_canonical_fits_keyword() {
        let mut bytes = fits_header(2, 2, 1, 16, false);
        set_fits_card(&mut bytes, 2, " NAXIS", "2");
        let error = inspect_fits(&bytes).expect_err("native decoder would see no NAXIS");
        assert!(error.contains("missing NAXIS"), "{error}");
    }

    #[test]
    fn uses_the_decoders_unicode_whitespace_rules_for_fits_keywords() {
        let mut bytes = fits_header(2, 2, 1, 16, true);
        let card = &mut bytes[5 * 80..6 * 80];
        card.fill(b' ');
        card[..10].copy_from_slice("NAXIS\u{a0} =0".as_bytes());
        let error = inspect_fits(&bytes).expect_err("duplicate ordinary Unicode-trimmed NAXIS");
        assert!(error.contains("duplicate structural"), "{error}");
        set_hierarch_card(&mut bytes, 5, "\u{a0}NAXIS\u{a0}", "0");
        let error = inspect_fits(&bytes).expect_err("duplicate HIERARCH Unicode-trimmed NAXIS");
        assert!(error.contains("duplicate structural"), "{error}");
    }

    #[test]
    fn noncanonical_fits_axis_names_cannot_replace_the_budgeted_dimensions() {
        for keyword in ["NAXIS01", "NAXIS+1"] {
            let mut bytes = fits_header(12_000, 8_000, 1, 16, true);
            set_fits_card(&mut bytes, 5, keyword, "1");
            let layout = inspect_fits(&bytes).expect("read canonical primary dimensions");
            assert_eq!((layout.width, layout.height), (12_000, 8_000));
        }
        let mut bytes = fits_header(2, 2, 3, 16, true);
        set_fits_card(&mut bytes, 5, "NAXIS3", "0");
        set_fits_card(&mut bytes, 6, "NAXIS03", "1");
        let error = inspect_fits(&bytes).expect_err("noncanonical axis cannot hide an empty one");
        assert!(
            error.contains("NAXIS3") && error.contains("not positive"),
            "{error}"
        );
    }

    #[test]
    fn parses_fits_values_without_a_space_after_the_equals_sign() {
        let mut bytes = fits_header(2, 2, 1, 16, false);
        let naxis_card = &mut bytes[2 * 80..3 * 80];
        naxis_card.fill(b' ');
        naxis_card[..11].copy_from_slice(b"NAXIS   =-2");
        let error = inspect_fits(&bytes).expect_err("negative NAXIS must not become positive");
        assert!(error.contains("primary NAXIS -2"), "{error}");

        let naxis_card = &mut bytes[2 * 80..3 * 80];
        naxis_card[..10].copy_from_slice(b"NAXIS   =2");
        naxis_card[10] = b' ';
        assert!(
            inspect_fits(&bytes).is_ok(),
            "a no-space positive NAXIS is valid"
        );
    }

    #[test]
    fn budgets_fits_sixteen_bit_samples_for_possible_float_promotion() {
        let bytes = fits_header(20_000, 12_000, 1, 16, false);
        assert_eq!(inspect_fits(&bytes).unwrap().bytes_per_sample, 4);
        // A two-byte estimate is below the 1.5 GiB budget; the possible f32
        // decode plus render buffers is above it, even without a payload.
        let error = validate_image_budget(&bytes).expect_err("promoted image exceeds budget");
        assert!(error.contains("working set"), "{error}");
        // XISF UInt16 remains an actual two-byte decoded sample.
        let xisf = xisf_header("20000:12000:1", "UInt16", "");
        assert_eq!(inspect_xisf(&xisf).unwrap().bytes_per_sample, 2);
        assert!(validate_image_budget(&xisf).is_ok());
    }

    #[test]
    fn rejects_rgb_fits_above_the_working_set_limit() {
        let error = validate_image_budget(&fits_header(20_000, 15_000, 3, 16, false))
            .expect_err("oversized RGB image");
        assert!(error.contains("working set"));
    }

    #[test]
    fn rejects_a_compressed_xisf_expansion_bomb() {
        let error =
            validate_image_budget(&xisf_header("50000:50000:3", "Float64", "zstd:60000000000"))
                .expect_err("compressed expansion bomb");
        assert!(error.contains("working set"));
    }
}
