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

    render_image(&image, max_dimension, max_dimension)
}

#[cfg(any(windows, test))]
pub(crate) fn render_preview(
    bytes: &[u8],
    max_width: u32,
    max_height: u32,
) -> Result<RenderedThumbnail, String> {
    if max_width == 0 || max_height == 0 {
        return Err("preview dimensions must be greater than zero".into());
    }
    validate_image_budget(bytes)?;

    let image = if bytes.starts_with(b"XISF0100") {
        seiza_xisf::from_bytes(bytes).map_err(|error| error.to_string())?
    } else {
        FitsImage::from_bytes(bytes).map_err(|error| error.to_string())?
    };

    render_image(&image, max_width, max_height)
}

fn render_image(
    image: &FitsImage,
    max_width: u32,
    max_height: u32,
) -> Result<RenderedThumbnail, String> {
    let width = u32::try_from(image.width).map_err(|_| "image width is too large")?;
    let height = u32::try_from(image.height).map_err(|_| "image height is too large")?;
    if width == 0 || height == 0 {
        return Err("image has no pixels".into());
    }

    let (output_width, output_height) = fitted_dimensions(width, height, max_width, max_height);
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

fn fitted_dimensions(width: u32, height: u32, max_width: u32, max_height: u32) -> (u32, u32) {
    if width <= max_width && height <= max_height {
        return (width, height);
    }

    let scale =
        (f64::from(max_width) / f64::from(width)).min(f64::from(max_height) / f64::from(height));
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
    let scale_x = f64::from(source_width) / f64::from(output_width);
    let scale_y = f64::from(source_height) / f64::from(output_height);
    for y in 0..output_height {
        let top = f64::from(y) * scale_y;
        let bottom = (f64::from(y + 1) * scale_y).min(f64::from(source_height));
        for x in 0..output_width {
            let left = f64::from(x) * scale_x;
            let right = (f64::from(x + 1) * scale_x).min(f64::from(source_width));
            let mut sum = [0.0; 3];
            // Average the whole footprint, including partial edge pixels.
            // Point sampling loses stars and leaves noise at full strength.
            for source_y in top.floor() as u32..bottom.ceil() as u32 {
                let wy = bottom.min(f64::from(source_y + 1)) - top.max(f64::from(source_y));
                for source_x in left.floor() as u32..right.ceil() as u32 {
                    let wx = right.min(f64::from(source_x + 1)) - left.max(f64::from(source_x));
                    let source_index =
                        source_y as usize * source_width as usize + source_x as usize;
                    let sample = pixel(source_index);
                    for channel in 0..3 {
                        sum[channel] += f64::from(sample[channel]) * wx * wy;
                    }
                }
            }
            let area = (right - left) * (bottom - top);
            bgra.extend(sum.map(|value| (value / area).round() as u8));
            bgra.push(u8::MAX);
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
    use super::{fitted_dimensions, render_preview, render_thumbnail, sample_bgra};
    use crate::{test_fits, test_xisf};

    #[test]
    fn dimensions_preserve_aspect_ratio_without_upscaling() {
        assert_eq!(fitted_dimensions(4000, 2000, 256, 256), (256, 128));
        assert_eq!(fitted_dimensions(2000, 4000, 256, 256), (128, 256));
        assert_eq!(fitted_dimensions(64, 32, 256, 256), (64, 32));
        assert_eq!(fitted_dimensions(4000, 2000, 160, 120), (160, 80));
        assert_eq!(fitted_dimensions(2000, 4000, 160, 120), (60, 120));
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

    #[test]
    fn preview_fits_rectangular_bounds() {
        let preview = render_preview(&test_fits(400, 200), 160, 120).expect("render preview");
        assert_eq!((preview.width, preview.height), (160, 80));
    }

    #[test]
    fn reduced_checkerboard_has_no_point_sampling_alias() {
        let thumbnail = sample_bgra(8, 8, 2, 2, |index| {
            let value = if (index / 8 + index % 8) % 2 == 0 {
                0
            } else {
                255
            };
            [value, value, value, 255]
        });
        assert_eq!(thumbnail.bgra, [128, 128, 128, 255].repeat(4));
    }

    #[test]
    fn fractional_reduction_preserves_channels_and_edge_contributions() {
        let thumbnail = sample_bgra(5, 1, 2, 1, |index| {
            [
                if index == 0 { 100 } else { 0 },
                50,
                if index == 2 { 200 } else { 0 },
                255,
            ]
        });
        assert_eq!(thumbnail.bgra, [40, 50, 40, 255, 0, 50, 40, 255]);
    }

    #[test]
    fn native_size_keeps_exact_samples() {
        let thumbnail = sample_bgra(3, 2, 3, 2, |index| [index as u8, 37, 99, 255]);
        let expected: Vec<_> = (0..6).flat_map(|index| [index, 37, 99, 255]).collect();
        assert_eq!(thumbnail.bgra, expected);
    }
}
