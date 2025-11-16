use ecow::eco_format;
use typst::diag::StrResult;
use typst::layout::PagedDocument;

/// An image format to export in.
pub enum ImageExportFormat {
    Png,
    Svg,
}

/// Export the frames to PNGs or SVGs.
fn export_image(
    document: &PagedDocument,
    fmt: ImageExportFormat,
    ppi: f32,
) -> StrResult<Vec<Vec<u8>>> {
    let mut buffers = Vec::new();
    for page in &document.pages {
        let buffer = match fmt {
            ImageExportFormat::Png => typst_render::render(page, ppi / 72.0)
                .encode_png()
                .map_err(|err| eco_format!("failed to write PNG file ({err})"))?,
            ImageExportFormat::Svg => {
                let svg = typst_svg::svg(page);
                svg.as_bytes().to_vec()
            }
        };
        buffers.push(buffer);
    }
    Ok(buffers)
}

/// Export to a PDF.
#[inline]
pub fn export_pdf(
    document: &PagedDocument,
    standards: &[typst_pdf::PdfStandard],
) -> StrResult<Vec<u8>> {
    let buffer = typst_pdf::pdf(
        document,
        &typst_pdf::PdfOptions {
            ident: typst::foundations::Smart::Auto,
            timestamp: None, // For reproducible builds
            standards: typst_pdf::PdfStandards::new(standards).unwrap_or_default(),
            ..Default::default()
        },
    )
    .map_err(|e| eco_format!("failed to export PDF: {:?}", e))?;
    Ok(buffer)
}

pub fn export(
    document: &PagedDocument,
    format: &str,
    ppi: f32,
    standards: &[typst_pdf::PdfStandard],
) -> StrResult<Vec<Vec<u8>>> {
    match format {
        "pdf" => export_pdf(document, standards).map(|pdf| vec![pdf]),
        "png" => export_image(document, ImageExportFormat::Png, ppi),
        "svg" => export_image(document, ImageExportFormat::Svg, ppi),
        _ => Err(eco_format!("unknown export format: {}", format)),
    }
}
