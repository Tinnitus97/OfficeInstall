<?php
/*********************************************************************
    tools/build-phar.php

    Packs this plugin directory into billing.phar.

    PHP refuses to write phar archives unless phar.readonly is off, and
    that setting cannot be changed at runtime - it has to come from the
    command line:

        php -d phar.readonly=0 tools/build-phar.php [target]

    The default target is ../billing.phar next to the plugin folder.
    Copy the result to include/plugins/billing.phar; on IIS run iisreset
    afterwards so OPcache picks it up.
**********************************************************************/

if (PHP_SAPI !== 'cli')
    exit("Run this from the command line.\n");

if (ini_get('phar.readonly')) {
    fwrite(STDERR, "phar.readonly is on - rerun as:\n"
        ."  php -d phar.readonly=0 ".$argv[0]."\n");
    exit(1);
}

$src = dirname(__DIR__);
$out = isset($argv[1]) ? $argv[1] : dirname($src).'/billing.phar';

// Everything that is part of the plugin at runtime. The build script and
// the packaging leftovers are not.
$skipDirs  = array('tools', '.git');
$skipFiles = array('.DS_Store', 'Thumbs.db');
$skipExts  = array('phar', 'zip', 'log');

$files = array();
$it = new RecursiveIteratorIterator(
    new RecursiveDirectoryIterator($src, FilesystemIterator::SKIP_DOTS),
    RecursiveIteratorIterator::SELF_FIRST);

foreach ($it as $file) {
    $rel = str_replace('\\', '/', substr($file->getPathname(), strlen($src) + 1));
    $top = explode('/', $rel)[0];
    if (in_array($top, $skipDirs, true))            continue;
    if (!$file->isFile())                            continue;
    if (in_array($file->getFilename(), $skipFiles, true)) continue;
    if (in_array(strtolower($file->getExtension()), $skipExts, true)) continue;
    $files[$rel] = $file->getPathname();
}

if (!isset($files['plugin.php']))
    exit("No plugin.php found in $src - is this the plugin directory?\n");

// A stale archive would otherwise be opened instead of rebuilt.
if (file_exists($out))
    unlink($out);

$phar = new Phar($out);
$phar->startBuffering();
foreach ($files as $rel => $path)
    $phar->addFile($path, $rel);

// osTicket never runs the stub: it reads the archive through the phar://
// stream wrapper and includes plugin.php directly. The stub only has to
// exist and be harmless.
$phar->setStub("<?php\n"
    ."/* osTicket plugin archive - loaded through the phar:// stream wrapper,\n"
    ."   not by running this file. */\n"
    ."__HALT_COMPILER();\n");

// osTicket calls Phar::getSignature() and skips any archive without one.
$phar->setSignatureAlgorithm(Phar::SHA256);
$phar->stopBuffering();

printf("%s\n  %d files, %s\n  SHA-256 %s\n",
    $out, count($files),
    number_format(filesize($out) / 1024, 1).' KiB',
    $phar->getSignature()['hash']);
