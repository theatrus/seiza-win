use seiza_fits::{FitsImage, StretchParams, statistics_u16, stretch_u16_to_u8};

use crate::limits::validate_image_budget;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderedThumbnail {
    pub width: u32,
    pub height: u32,
    /// Top-down, row-major BGRA pixels with opaque alpha.
    pub bgra: Vec<u8>,
}

pub fn render_thumbnail(bytes: &[u8], max_dimension: u32) -> Result<RenderedThumbnail, String> {
    if max_dimension == 0 {
        return Err("thumbnail dimension must be greater than zero".into());
    }
    validate_image_budget(bytes)?;

    let image = if bytes.starts_with(b"XISF0100") {
        seiza_xisf::from_bytes(bytes).map_err(|error| error.to_string())?
    } else {
        FitsImage::from_bytes(bytes).map_err(|error| error.to_string())?
    };

    render_image(&image, max_dimension)
}

fn render_image(image: &FitsImage, max_dimension: u32) -> Result<RenderedThumbnail, String> {
    let width = u32::try_from(image.width).map_err(|_| "image width is too large")?;
    let height = u32::try_from(image.height).map_err(|_| "image height is too large")?;
    if width == 0 || height == 0 {
        return Err("image has no pixels".into());
    }

    let (output_width, output_height) = thumbnail_dimensions(width, height, max_dimension);
    let params = StretchParams::default();

    if let Some(rgb) = image.debayer().or_else(|| image.rgb_planes()) {
        let pixel_count = rgb
            .width
            .checked_mul(rgb.height)
            .ok_or("image is too large")?;
        if rgb.data.len() != pixel_count.saturating_mul(3) {
            return Err("invalid RGB image buffer".into());
        }

        let mut channels = [
            Vec::with_capacity(pixel_count),
            Vec::with_capacity(pixel_count),
            Vec::with_capacity(pixel_count),
        ];
        for pixel in rgb.data.chunks_exact(3) {
            channels[0].push(pixel[0]);
            channels[1].push(pixel[1]);
            channels[2].push(pixel[2]);
        }
        let stretched = channels.map(|channel| {
            let statistics = statistics_u16(&channel);
            stretch_u16_to_u8(&channel, &statistics, &params)
        });

        Ok(sample_bgra(
            width,
            height,
            output_width,
            output_height,
            |source_index| {
                [
                    stretched[2][source_index],
                    stretched[1][source_index],
                    stretched[0][source_index],
                    u8::MAX,
                ]
            },
        ))
    } else {
        let stretched = image.stretch_to_u8(&params);
        let expected = image
            .width
            .checked_mul(image.height)
            .ok_or("image is too large")?;
        if stretched.len() != expected {
            return Err("invalid monochrome image buffer".into());
        }

        Ok(sample_bgra(
            width,
            height,
            output_width,
            output_height,
            |source_index| {
                let value = stretched[source_index];
                [value, value, value, u8::MAX]
            },
        ))
    }
}

fn thumbnail_dimensions(width: u32, height: u32, max_dimension: u32) -> (u32, u32) {
    let largest = width.max(height);
    if largest <= max_dimension {
        return (width, height);
    }

    let scale = f64::from(max_dimension) / f64::from(largest);
    (
        (f64::from(width) * scale).round().max(1.0) as u32,
        (f64::from(height) * scale).round().max(1.0) as u32,
    )
}

fn sample_bgra(
    source_width: u32,
    source_height: u32,
    output_width: u32,
    output_height: u32,
    pixel: impl Fn(usize) -> [u8; 4],
) -> RenderedThumbnail {
    let mut bgra = Vec::with_capacity(output_width as usize * output_height as usize * 4);
    for y in 0..output_height {
        let source_y = u64::from(y) * u64::from(source_height) / u64::from(output_height);
        for x in 0..output_width {
            let source_x = u64::from(x) * u64::from(source_width) / u64::from(output_width);
            let source_index = (source_y * u64::from(source_width) + source_x) as usize;
            bgra.extend_from_slice(&pixel(source_index));
        }
    }

    RenderedThumbnail {
        width: output_width,
        height: output_height,
        bgra,
    }
}

#[cfg(test)]
mod tests {
    use super::{render_thumbnail, thumbnail_dimensions};
    use crate::{test_fits, test_xisf};

    #[test]
    fn dimensions_preserve_aspect_ratio_without_upscaling() {
        assert_eq!(thumbnail_dimensions(4000, 2000, 256), (256, 128));
        assert_eq!(thumbnail_dimensions(2000, 4000, 256), (128, 256));
        assert_eq!(thumbnail_dimensions(64, 32, 256), (64, 32));
    }

    #[test]
    fn renders_a_fits_thumbnail_as_opaque_bgra() {
        let thumbnail = render_thumbnail(&test_fits(4, 2), 2).expect("render FITS");
        assert_eq!((thumbnail.width, thumbnail.height), (2, 1));
        assert_eq!(thumbnail.bgra.len(), 8);
        assert!(
            thumbnail.bgra.chunks_exact(4).all(|pixel| {
                pixel[0] == pixel[1] && pixel[1] == pixel[2] && pixel[3] == u8::MAX
            })
        );
    }

    #[test]
    fn rejects_unknown_data() {
        assert!(render_thumbnail(b"not an astronomy image", 256).is_err());
    }

    #[test]
    fn renders_an_xisf_thumbnail() {
        let thumbnail = render_thumbnail(&test_xisf(3, 6), 3).expect("render XISF");
        assert_eq!((thumbnail.width, thumbnail.height), (2, 3));
        assert_eq!(thumbnail.bgra.len(), 24);
    }
}
