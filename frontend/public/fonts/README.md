# Heading font

Bricolage Grotesque, variable weight, width and optical size.

Source: https://github.com/google/fonts/tree/main/ofl/bricolagegrotesque

The unmodified variable font is bundled locally so Maki does not request fonts from
an external service. See BricolageGrotesque-LICENSE.txt for the SIL Open Font License.

Two files, same face: the WOFF2 is what browsers actually load (about 200 KB), and the
TTF it was compressed from (about 400 KB) stays as the `@font-face` fallback. Regenerate
the WOFF2 after replacing the TTF:

    python -c "from fontTools.ttLib import TTFont; f=TTFont('BricolageGrotesque-Variable.ttf'); f.flavor='woff2'; f.save('BricolageGrotesque-Variable.woff2')"

