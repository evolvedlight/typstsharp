fn main() {
    let s = "v-1.7";
    let standard: Result<typst_pdf::PdfStandard, _> = s.parse();
    println!("{:?}", standard.is_ok());
}
