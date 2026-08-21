<?php
/*********************************************************************
    class.BillingPdf.php

    PDF document for the Billing plugin's report export.

    Extends FPDF with a configurable letterhead (logo, title, subtitle,
    free text) and a configurable footer, both driven by the plugin
    settings. Because the column heading row is drawn from Header(), it
    repeats automatically on every page - including after page breaks.

    Adapted and maintained by tinnitus-ost.
 **********************************************************************/
if (!defined('INCLUDE_DIR')) die('Access Denied');

require_once(__DIR__.'/lib/fpdf/fpdf.php');

class BillingPdf extends FPDF {

    /* -- letterhead ------------------------------------------------- */
    public $bLogo       = '';      // absolute path or URL of the logo
    public $bLogoWidth  = 35;      // mm
    public $bLogoType   = '';      // explicit type when reading from a temp file
    public $bTitle      = '';
    public $bSubtitle   = '';
    public $bHeaderText = '';      // free text under the title
    public $bMeta       = '';      // generated / filter summary line

    /* -- layout ------------------------------------------------------ */
    public $bLogoAlign  = 'left';   // left | center | right
    public $bBesideLogo = true;     // text next to the logo?
    public $bTextAlign  = 'left';   // title/subtitle/header/meta alignment
    public $bTitleSize  = 14, $bSubtitleSize = 10, $bTextSize = 8;

    /* -- footer ----------------------------------------------------- */
    public $bFooterText = '';
    public $bPageNumbers = true;
    public $bPageLabel   = 'Page %s / %s';

    /* -- table ------------------------------------------------------ */
    public $bColumns   = array();  // [ ['key'=>.., 'label'=>..], .. ]
    public $bWidths    = array();  // key => width in mm
    public $bFontSize  = 8;
    public $bEncoder   = null;     // callable for UTF-8 -> CP1252

    private $bHeadHeight = 0;      // measured letterhead height

    /**
     * SetFont that never aborts the export: if a style variant has no font
     * definition (e.g. bold-italic), fall back to a simpler style instead of
     * letting FPDF raise a fatal error.
     */
    function bSetFont($style, $size) {
        $style = strtoupper((string) $style);
        $tries = array($style);
        if (strpos($style, 'B') !== false && strpos($style, 'I') !== false)
            $tries[] = str_replace('I', '', $style);      // BI -> B
        $tries[] = str_replace(array('B', 'I'), '', $style); // -> plain / U
        foreach ($tries as $t) {
            try {
                $this->SetFont('Helvetica', $t, $size);
                return;
            } catch (Exception $e) {
                // try the next, simpler style
            }
        }
    }

    function bEnc($s) {
        $fn = $this->bEncoder;
        return $fn ? $fn($s) : $s;
    }

    /**
     * Minimal HTML renderer for the rich-text fields (osTicket redactor).
     * Supports <b>/<strong>, <i>/<em>, <u>, <br>, and block alignment via
     * <p>/<div align=..> or style="text-align:..". Everything else is
     * stripped, so unexpected markup can never break the export.
     *
     * Returns array of blocks: array('align'=>'L|C|R', 'runs'=>array(...)).
     */
    static function bParseHtml($html) {
        $html = (string) $html;
        if ($html === '')
            return array();
        // normalise line breaks and block boundaries
        $html = preg_replace('#<br\s*/?>#i', "\n", $html);
        $html = preg_replace('#</(p|div|h[1-6])>#i', "\n", $html);

        $blocks = array();
        // split on opening block tags, keeping their attributes
        $parts = preg_split('#(<(?:p|div|h[1-6])\b[^>]*>)#i', $html, -1, PREG_SPLIT_DELIM_CAPTURE);
        // Track alignment as a stack so an outer <div style="text-align:center">
        // still applies to inner <p> blocks (which redactor emits). An inner
        // block only overrides when it carries its own alignment.
        $stack = array('L');
        foreach ($parts as $part) {
            if (preg_match('#^<(?:p|div|h[1-6])\b([^>]*)>$#i', $part, $m)) {
                $attr = $m[1];
                $inherited = end($stack);
                $align = $inherited;
                if (preg_match('#text-align\s*:\s*(left|center|right|justify)#i', $attr, $a)
                        || preg_match('#align\s*=\s*["\']?(left|center|right)#i', $attr, $a)) {
                    $v = strtolower($a[1]);
                    $align = $v === 'center' ? 'C' : ($v === 'right' ? 'R' : 'L');
                }
                $stack[] = $align;
                continue;
            }
            $align = end($stack);
            foreach (explode("\n", $part) as $line) {
                $runs = self::bParseRuns($line);
                if ($runs !== null)
                    $blocks[] = array('align' => $align, 'runs' => $runs);
            }
        }
        return $blocks;
    }

    /** Split one line into styled runs; null when the line is empty. */
    private static function bParseRuns($line) {
        // Tokenise on any inline tag we understand. Everything else is text.
        $tokens = preg_split('#(</?(?:b|strong|i|em|u|s|strike|del|span|font|a)\b[^>]*>)#i',
            $line, -1, PREG_SPLIT_DELIM_CAPTURE);
        // style stacks so nested tags combine and close correctly
        $bold = 0; $ital = 0; $undl = 0;
        $colors = array();                       // stack of active colours
        $runs = array(); $any = false;

        foreach ($tokens as $t) {
            if (preg_match('#^<(/?)(b|strong|i|em|u|s|strike|del|span|font|a)\b([^>]*)>$#i', $t, $m)) {
                $close = ($m[1] === '/');
                $tag   = strtolower($m[2]);
                $attr  = $m[3];
                switch ($tag) {
                    case 'b': case 'strong': $bold += $close ? -1 : 1; break;
                    case 'i': case 'em':     $ital += $close ? -1 : 1; break;
                    case 'u':                $undl += $close ? -1 : 1; break;
                    case 'a':                $undl += $close ? -1 : 1;  // links: underline
                        // fall through to pick up an inline colour on the link
                    case 'span': case 'font':
                        if ($close) {
                            if (!empty($colors)) array_pop($colors);
                            // span/font may also carry weight/style
                            if ($tag !== 'a') { /* handled by push below */ }
                        } else {
                            $col = self::bParseColor($attr);
                            // a colour is always pushed (null keeps the stack aligned)
                            $colors[] = $col !== null ? $col
                                      : (empty($colors) ? null : end($colors));
                            if (preg_match('#font-weight\s*:\s*(bold|[6-9]00)#i', $attr)) $bold++;
                            if (preg_match('#font-style\s*:\s*italic#i', $attr))          $ital++;
                            if (preg_match('#text-decoration\s*:\s*underline#i', $attr))  $undl++;
                        }
                        break;
                }
                continue;
            }
            $txt = html_entity_decode(strip_tags($t), ENT_QUOTES, 'UTF-8');
            if ($txt === '') continue;
            if (trim($txt) !== '') $any = true;
            $col = null;
            for ($k = count($colors) - 1; $k >= 0; $k--)
                if ($colors[$k] !== null) { $col = $colors[$k]; break; }
            $runs[] = array(
                'text' => $txt,
                'b' => $bold > 0, 'i' => $ital > 0, 'u' => $undl > 0,
                'color' => $col,
            );
        }
        return $any ? $runs : null;
    }

    /** Extract an RGB colour from a style/attr string, or null. */
    private static function bParseColor($attr) {
        if (preg_match('#color\s*:\s*#i', $attr)
                && preg_match('#color\s*:\s*(\#[0-9a-f]{3,6}|rgb\([^)]+\))#i', $attr, $m))
            return self::bColorToRgb($m[1]);
        if (preg_match('#\bcolor\s*=\s*["\']?(\#?[0-9a-z]+)#i', $attr, $m))
            return self::bColorToRgb($m[1]);
        return null;
    }

    private static function bColorToRgb($c) {
        $c = trim($c);
        if (stripos($c, 'rgb') === 0 && preg_match('#(\d+)\D+(\d+)\D+(\d+)#', $c, $m))
            return array((int)$m[1], (int)$m[2], (int)$m[3]);
        $c = ltrim($c, '#');
        if (strlen($c) === 3)
            $c = $c[0].$c[0].$c[1].$c[1].$c[2].$c[2];
        if (strlen($c) === 6 && ctype_xdigit($c))
            return array(hexdec(substr($c,0,2)), hexdec(substr($c,2,2)), hexdec(substr($c,4,2)));
        return null;
    }

    /** Draw parsed HTML blocks at the current position. */
    function bWriteHtml($html, $w, $lineH = 4.5, $size = null) {
        $blocks = self::bParseHtml($html);
        if (!$blocks) return;
        $size = $size ?: $this->bTextSize;
        $x = $this->GetX();
        foreach ($blocks as $blk) {
            // measure the line using each run's real style for accurate centring
            $lineW = 0;
            foreach ($blk['runs'] as $r) {
                $this->bSetFont(($r['b'] ? 'B' : '').($r['i'] ? 'I' : ''), $size);
                $lineW += $this->GetStringWidth($this->bEnc($r['text']));
            }
            $startX = $x;
            if ($blk['align'] === 'C') $startX = $x + max(0, ($w - $lineW) / 2);
            elseif ($blk['align'] === 'R') $startX = $x + max(0, $w - $lineW);
            $this->SetX($startX);
            foreach ($blk['runs'] as $r) {
                $style = ($r['b'] ? 'B' : '').($r['i'] ? 'I' : '').($r['u'] ? 'U' : '');
                $this->bSetFont($style, $size);
                if (!empty($r['color']))
                    $this->SetTextColor($r['color'][0], $r['color'][1], $r['color'][2]);
                $this->Cell($this->GetStringWidth($this->bEnc($r['text'])), $lineH,
                    $this->bEnc($r['text']), 0, 0);
                if (!empty($r['color']))
                    $this->SetTextColor(0, 0, 0);
            }
            $this->Ln($lineH);
            $this->SetX($x);
        }
        $this->SetFont('Helvetica', '', $size);
    }

    private function bAlign($a) {
        return $a === 'center' ? 'C' : ($a === 'right' ? 'R' : 'L');
    }

    function Header() {
        $top = $this->tMargin;
        $pageW = $this->w - $this->lMargin - $this->rMargin;

        // ---- logo (original size, only scaled down when too wide) ----
        $logoBottom = $top;
        $textX = $this->lMargin;
        if ($this->bLogo) {
            $lw = $this->bLogoNaturalWidth($pageW);
            $lx = $this->lMargin;
            if ($this->bLogoAlign === 'center')
                $lx = $this->lMargin + ($pageW - $lw) / 2;
            elseif ($this->bLogoAlign === 'right')
                $lx = $this->w - $this->rMargin - $lw;
            try {
                if ($this->bLogoType)
                    $this->Image($this->bLogo, $lx, $top, $lw, 0, $this->bLogoType);
                else
                    $this->Image($this->bLogo, $lx, $top, $lw);
                $logoBottom = $top + $this->bLogoHeight;
                if ($this->bBesideLogo && $this->bLogoAlign === 'left')
                    $textX = $lx + $lw + 6;
            } catch (Exception $e) {
                // a broken or unreadable logo must never break the export
            }
        }

        // ---- text block ---------------------------------------------
        $y = $top;
        if (!$this->bBesideLogo && $this->bLogo && $y < $logoBottom)
            $y = $logoBottom + 2;
        $this->SetXY($textX, $y);
        $w = $this->w - $this->rMargin - $textX;

        // full content width, used for centre/right so the text lines up with
        // the table and the footer instead of the narrower area next to the logo
        $fullX = $this->lMargin;
        $fullW = $this->w - $this->lMargin - $this->rMargin;
        $ta = array('left' => 'L', 'center' => 'C', 'right' => 'R');
        $al = isset($ta[$this->bTextAlign]) ? $ta[$this->bTextAlign] : 'L';

        // when centred or right aligned, span the whole page width
        $useFull = ($al !== 'L');
        $bx = $useFull ? $fullX : $textX;
        $bw = $useFull ? $fullW : $w;
        if ($useFull) $this->SetXY($bx, $y);

        if ($this->bTitle !== '') {
            $this->SetFont('Helvetica', 'B', $this->bTitleSize);
            $this->Cell($bw, $this->bTitleSize * 0.6, $this->bEnc($this->bTitle), 0, 1, $al);
            $this->SetX($bx);
        }
        if ($this->bSubtitle !== '') {
            $this->SetFont('Helvetica', '', $this->bSubtitleSize);
            $this->Cell($bw, $this->bSubtitleSize * 0.6, $this->bEnc($this->bSubtitle), 0, 1, $al);
            $this->SetX($bx);
        }
        if ($this->bHeaderText !== '') {
            $this->SetX($bx);
            $this->bWriteHtml($this->bHeaderText, $bw, 4.5, $this->bTextSize);
        }
        if ($this->bMeta !== '') {
            $this->SetX($bx);
            $this->SetFont('Helvetica', '', $this->bTextSize);
            $this->SetTextColor(90, 90, 90);
            $this->Cell($bw, 5, $this->bEnc($this->bMeta), 0, 1, $al);
            $this->SetTextColor(0, 0, 0);
        }

        if ($this->GetY() < $logoBottom)
            $this->SetY($logoBottom);
        $this->Ln(2);
        $this->SetX($this->lMargin);

        $this->bTableHead();
    }

    /**
     * Physical resolution of an image in dpi.
     * Reads the real value from the file (PNG pHYs chunk, JPEG JFIF density
     * or EXIF) so the logo prints at its true size instead of a guessed one.
     * Falls back to 96 dpi, the usual screen resolution.
     */
    static function bImageDpi($path) {
        $fallback = 96.0;
        $dpi = self::bReadDpi($path, $fallback);
        // implausible values (e.g. JFIF density 1) would blow up the logo,
        // which then gets clamped to the max width and looks oversized
        return ($dpi >= 30 && $dpi <= 1200) ? $dpi : $fallback;
    }

    private static function bReadDpi($path, $fallback) {
        $data = @file_get_contents($path, false, null, 0, 65536);
        if ($data === false || strlen($data) < 24)
            return $fallback;

        // --- PNG: pHYs chunk holds pixels per metre --------------------
        if (substr($data, 0, 8) === "\x89PNG\r\n\x1a\n") {
            $off = 8;
            while ($off + 8 <= strlen($data)) {
                $len  = unpack('N', substr($data, $off, 4))[1];
                $type = substr($data, $off + 4, 4);
                if ($type === 'pHYs' && $off + 8 + 9 <= strlen($data)) {
                    $x    = unpack('N', substr($data, $off + 8, 4))[1];
                    $unit = ord(substr($data, $off + 16, 1));
                    if ($unit === 1 && $x > 0)          // 1 = metre
                        return $x * 0.0254;             // px/m -> dpi
                    return $fallback;
                }
                if ($type === 'IDAT' || $type === 'IEND')
                    break;
                $off += 12 + $len;
            }
            return $fallback;
        }

        // --- JPEG: JFIF APP0 density ----------------------------------
        if (substr($data, 0, 2) === "\xFF\xD8") {
            $p = strpos($data, "JFIF\x00");
            if ($p !== false && $p + 12 <= strlen($data)) {
                $unit = ord($data[$p + 7]);                       // 1=dpi 2=dpcm
                $x    = unpack('n', substr($data, $p + 8, 2))[1];
                if ($x > 0) {
                    if ($unit === 1) return (float) $x;
                    if ($unit === 2) return $x * 2.54;
                }
            }
            if (function_exists('exif_read_data')) {
                $e = @exif_read_data($path);
                if ($e && !empty($e['XResolution'])) {
                    $parts = explode('/', (string) $e['XResolution']);
                    $val = count($parts) === 2 && $parts[1] != 0
                        ? $parts[0] / $parts[1] : (float) $parts[0];
                    $unit = isset($e['ResolutionUnit']) ? (int) $e['ResolutionUnit'] : 2;
                    if ($val > 0)
                        return $unit === 3 ? $val * 2.54 : (float) $val;
                }
            }
        }
        return $fallback;
    }

    /**
     * Logo width in mm at its ORIGINAL size (pixels / dpi), only scaled down
     * when it would not fit the page. Never enlarged.
     */
    public $bLogoHeight = 0;
    public $bLogoMaxWidth = 45;   // mm - letterhead logos rarely need more
    private function bLogoNaturalWidth($maxW) {
        $info = @getimagesize($this->bLogo);
        if (!$info || empty($info[0])) {
            $this->bLogoHeight = 12;
            return min(35, $maxW);
        }
        $wPx = (int) $info[0];
        $hPx = (int) $info[1];
        $dpi = self::bImageDpi($this->bLogo);
        if ($dpi <= 0) $dpi = 96.0;

        $mm = $wPx / $dpi * 25.4;                 // true physical width
        $limit = min($maxW, $this->bLogoMaxWidth); // keep the letterhead sane
        if ($mm > $limit) $mm = $limit;           // shrink only, never enlarge
        $this->bLogoHeight = $hPx > 0 ? $mm * ($hPx / $wPx) : 12;
        return $mm;
    }

    /** Column heading row - repeated on every page. */
    function bTableHead() {
        if (!$this->bColumns)
            return;
        $this->SetFont('Helvetica', 'B', $this->bFontSize);
        $this->SetFillColor(230, 230, 230);
        foreach ($this->bColumns as $col) {
            $k = $col['key'];
            $w = isset($this->bWidths[$k]) ? $this->bWidths[$k] : 20;
            $this->Cell($w, 6, $this->bEnc(html_entity_decode($col['label'], ENT_QUOTES, 'UTF-8')),
                1, 0, 'C', true);
        }
        $this->Ln();
        $this->SetFont('Helvetica', '', $this->bFontSize);
    }

    function Footer() {
        $this->SetY(-14);
        $this->SetTextColor(110, 110, 110);
        if ($this->bFooterText !== '') {
            $this->SetX($this->lMargin);
            $this->bWriteHtml($this->bFooterText,
                $this->w - $this->lMargin - $this->rMargin - 25, 3.6, 7);
        }
        if ($this->bPageNumbers) {
            $this->SetY(-11);
            $this->SetFont('Helvetica', '', 7);
            $this->Cell(0, 4, $this->bEnc(sprintf($this->bPageLabel,
                $this->PageNo(), '{nb}')), 0, 0, 'R');
        }
        $this->SetTextColor(0, 0, 0);
    }
}
