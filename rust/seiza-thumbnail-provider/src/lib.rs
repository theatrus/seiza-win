//! Native Windows Explorer thumbnail provider for Seiza image formats.

#[cfg(windows)]
mod com_server;
mod limits;
mod renderer;

pub use renderer::{RenderedThumbnail, render_thumbnail};

#[cfg(test)]
pub(crate) fn test_fits(width: u32, height: u32) -> Vec<u8> {
    fn card(keyword: &str, value: &str) -> [u8; 80] {
        let mut card = [b' '; 80];
        let text = format!("{keyword:<8}= {value:>20}");
        card[..text.len()].copy_from_slice(text.as_bytes());
        card
    }

    let mut bytes = Vec::with_capacity(5760);
    bytes.extend_from_slice(&card("SIMPLE", "T"));
    bytes.extend_from_slice(&card("BITPIX", "16"));
    bytes.extend_from_slice(&card("NAXIS", "2"));
    bytes.extend_from_slice(&card("NAXIS1", &width.to_string()));
    bytes.extend_from_slice(&card("NAXIS2", &height.to_string()));
    bytes.extend_from_slice(&card("BZERO", "32768"));
    let mut end = [b' '; 80];
    end[..3].copy_from_slice(b"END");
    bytes.extend_from_slice(&end);
    bytes.resize(2880, b' ');

    for index in 0..width as usize * height as usize {
        let sample = ((index * 65535) / (width as usize * height as usize).max(1)) as u16;
        let stored = sample.wrapping_sub(32768) as i16;
        bytes.extend_from_slice(&stored.to_be_bytes());
    }
    let padded_length = bytes.len().div_ceil(2880) * 2880;
    bytes.resize(padded_length, 0);
    bytes
}

#[cfg(test)]
pub(crate) fn test_xisf(width: u32, height: u32) -> Vec<u8> {
    let pixels = (0..width as usize * height as usize)
        .map(|index| (index % 256) as u8)
        .collect::<Vec<_>>();
    let mut offset = 0u64;
    loop {
        let image = format!(
            "<Image geometry=\"{width}:{height}:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"attachment:{offset}:{}\"/>",
            pixels.len()
        );
        let header = format!(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\">{image}</xisf>"
        );
        let next_offset = 16 + header.len() as u64;
        if next_offset == offset {
            let mut bytes = Vec::with_capacity(next_offset as usize + pixels.len());
            bytes.extend_from_slice(b"XISF0100");
            bytes.extend_from_slice(&(header.len() as u32).to_le_bytes());
            bytes.extend_from_slice(&[0; 4]);
            bytes.extend_from_slice(header.as_bytes());
            bytes.extend_from_slice(&pixels);
            return bytes;
        }
        offset = next_offset;
    }
}
