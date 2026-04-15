The game has a bug where when a SubPart is added to an existing Part in the vehicle editor that it is not rendered visually.  The part is there in the data, and when the vehicle is saved or launched, the SubPart appears.

But during vehicle editing, it is invisible to rendering.

The following is a quote from the KSA game developers about what might be the problem:

> You could try calling the PartTree of the Part's ReinitializeDerivedValues() function. > Probably fairly heavy though. 
> 
> Essentially, when you add a SubPart to a Part it needs to tell the PartTree that it exists now > and its modules need to be added. That function will recompute the entire PartTree (iirc) > which is probably more expensive than needed. 
> 
> It does seem that if you use Part's AddSubPart() it will add the SubParts Modules to the Part correctly but those Modules need to be propagated to the PartTree so they can be found/iterated over I believe.

Do a deep dive analysis of the decompiled KSA sources under decomp/ and how this mod works and make a plan into FIX_SUBPARTS_PLAN.md about how to fix the issue with detailed tasks for the refactors needed.

I just want to isolate this fix to be user-initiated by pressing the "Refresh Vehicle" button that is a placeholder for this fix in the BuilderPlus.cs DrawRefreshVehicleWindow function