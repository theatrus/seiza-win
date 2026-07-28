use quick_xml::Reader;
use quick_xml::events::{BytesStart, Event};

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
    let mut width = None;
    let mut height = None;
    let mut third_axis = None;
    let mut bayer = false;
    let header_limit = bytes.len().min(MAX_HEADER_BYTES);
    let mut found_end = false;

    for card in bytes[..header_limit].chunks_exact(80) {
        let keyword = trim_ascii(&card[..8]);
        if keyword == b"END" {
            found_end = true;
            break;
        }
        if card[8] != b'=' {
            continue;
        }
        let value = trim_ascii(
            card[10..]
                .split(|byte| *byte == b'/')
                .next()
                .unwrap_or_default(),
        );
        match keyword {
            b"BITPIX" => bitpix = parse_i64(value),
            b"NAXIS" => naxis = parse_i64(value),
            b"NAXIS1" => width = parse_positive_u64(value),
            b"NAXIS2" => height = parse_positive_u64(value),
            b"NAXIS3" => third_axis = parse_positive_u64(value),
            b"BAYERPAT" => bayer = !value.is_empty(),
            _ => {}
        }
    }

    if !found_end {
        return Err("FITS header is missing END or exceeds 16 MiB".into());
    }
    let bitpix = bitpix.ok_or("FITS header is missing BITPIX")?;
    let bytes_per_sample = match bitpix {
        8 => 1,
        16 => 2,
        32 | -32 => 4,
        -64 => 8,
        _ => return Err(format!("unsupported FITS BITPIX {bitpix}")),
    };
    let planes = if naxis.unwrap_or_default() >= 3 {
        third_axis.unwrap_or(1).clamp(1, 3)
    } else {
        1
    };

    Ok(ImageLayout {
        width: width.ok_or("FITS header is missing NAXIS1")?,
        height: height.ok_or("FITS header is missing NAXIS2")?,
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
                if element.local_name().as_ref() == b"Image" && layout.is_none() =>
            {
                layout = Some(xisf_image_layout(&element)?);
                inside_first_image = true;
            }
            Event::Empty(element)
                if element.local_name().as_ref() == b"Image" && layout.is_none() =>
            {
                layout = Some(xisf_image_layout(&element)?);
                break;
            }
            Event::Start(element) | Event::Empty(element)
                if inside_first_image && element.local_name().as_ref() == b"ColorFilterArray" =>
            {
                if let Some(layout) = &mut layout {
                    layout.bayer = true;
                }
            }
            Event::End(element)
                if inside_first_image && element.local_name().as_ref() == b"Image" =>
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
        let value = std::str::from_utf8(attribute.value.as_ref())
            .map_err(|_| "XISF image attribute is not UTF-8")?;
        match attribute.key.as_ref() {
            b"geometry" => geometry = Some(value.to_string()),
            b"sampleFormat" => sample_format = Some(value.to_string()),
            b"compression" => compressed = true,
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
    use super::validate_image_budget;

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
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"XISF0100");
        bytes.extend_from_slice(&(header.len() as u32).to_le_bytes());
        bytes.extend_from_slice(&[0; 4]);
        bytes.extend_from_slice(header.as_bytes());
        bytes
    }

    #[test]
    fn accepts_representative_large_mono_fits() {
        assert!(validate_image_budget(&fits_header(12_000, 8_000, 1, 16, false)).is_ok());
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
