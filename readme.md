# Overview

This repo contains a language agnostic parser for [Microsoft's Windows SDK Metadata](https://github.com/microsoft/win32metadata). As presented, the app also contains an output stage which produces headers for Freebasic.

# Rationale

All of the listed current 'projections' (for that is what different language output of the metadata is called apparently) in the readme of the repo above are for 'modern' languages that are either multi-pass or holistic in their compilation approach. IE, the order that the types are output doesn't matter, as long as the types needed are available.

Freebasic on the other hand is an old school language/compiler, which like C, requires that you can only use types that the compiler has already seen. IE the order of the types really does matter. This is a big stumbling block for this metadata, as the order as scraped from the SDK headers (which cater to a similar limitation as Freebasic has) is lost and replaced with a namespace approach. The problem with this is that the metadata has circular namespace dependencies, and circular dependencies aren't something one-pass languages can do.

The project is then basically split into two parts, the collection and organisation of functions, enums, constants which is language agnostic, and then the output stage.

# So what?

Well, this means that ideally to generate a 'projection' for a different one-pass, top down language, you don't have to bother with code to order all the types track forward declarations etc, since that's all been done. All the work that needs to be done is in FileOutput.cs. Change how structs look in your language, change how interfaces look, turn off function overloads if you have to and that's it (ideally, because obviously I haven't done it).

It's not quite like that though. All the parts that need to be changed for a different language are marked as FB SPECIFIC in comments in the code. It's not all confined to FileOutput.cs because Arrays and Pointer output are handled in the type info for those things, constant strings have bits that need escaping, the way hex constants are formatted, etc