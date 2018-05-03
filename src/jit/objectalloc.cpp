// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XX                                                                           XX
XX                         ObjectAllocator                                   XX
XX                                                                           XX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
*/

#include "jitpch.h"
#ifdef _MSC_VER
#pragma hdrstop
#endif

#include "gentree.h"

//------------------------------------------------------------------------
// DoPhase: Run analysis (if object stack allocation is enabled) and then
//          morph each GT_ALLOCOBJ node either into an allocation helper
//          call or stack allocation.
// Notes:
//    Runs only if Compiler::optMethodFlags has flag OMF_HAS_NEWOBJ set.

void ObjectAllocator::DoPhase()
{
    JITDUMP("\n*** ObjectAllocationPhase: ");
    if ((comp->optMethodFlags & OMF_HAS_NEWOBJ) == 0)
    {
        JITDUMP("no newobjs in this method; punting\n");
        return;
    }

    if (IsObjectStackAllocationEnabled())
    {
        JITDUMP("enabled, analyzing...\n");
        DoAnalysis();
    }
    else
    {
        JITDUMP("disabled, punting\n");
    }

    const bool didStackAllocate = MorphAllocObjNodes();

    if (didStackAllocate)
    {
        RewriteUses();
    }
}

struct ObjectAllocator::BuildConnGraphVisitorCallbackData
{
    BuildConnGraphVisitorCallbackData(ObjectAllocator* caller, BitVec* connGraphPointees)
        : m_caller(caller), m_connGraphPointees(connGraphPointees)
    {
    }

    void MarkLclVarAsNonStackAlloc(unsigned int lclNum)
    {
        BitVecOps::AddElemD(&m_caller->m_bitVecTraits, m_caller->m_EscapingPointers, lclNum);
    }

    bool IsLclVarNonStackAlloc(unsigned int lclNum)
    {
        return BitVecOps::IsMember(&m_caller->m_bitVecTraits, m_caller->m_EscapingPointers, lclNum);
    }

    void SetPointerPointeeRel(unsigned int pointerLclNum, unsigned int pointeeLclNum)
    {
        BitVecOps::AddElemD(&m_caller->m_bitVecTraits, m_connGraphPointees[pointerLclNum], pointeeLclNum);
    }

private:
    ObjectAllocator* m_caller;
    BitVec*          m_connGraphPointees;
};

//------------------------------------------------------------------------
// DoAnalysis: Walk over basic blocks of the method and detect all local
//             variables that can be allocated on the stack.
//
// Assumptions:
//    Must be run after the dominators have been computed (we need this
//    information to detect loops).
void ObjectAllocator::DoAnalysis()
{
    assert(m_IsObjectStackAllocationEnabled);
    // assert(comp->fgDomsComputed);
    assert(!m_AnalysisDone);

    if (comp->lvaCount > 0)
    {
        m_EscapingPointers = BitVecOps::MakeEmpty(&m_bitVecTraits);

        BitVec* connGraphPointees;
        BitVec* connGraphPointers;

        BuildConnGraph(&connGraphPointees);
        ComputeReachableNodes(&m_bitVecTraits, connGraphPointees, m_EscapingPointers);
    }

    m_AnalysisDone = true;
}

void ObjectAllocator::BuildConnGraph(BitVec** pConnGraphPointees)
{
    assert(pConnGraphPointees);

    *pConnGraphPointees       = new (comp->getAllocator()) BitSetShortLongRep[comp->lvaCount];
    BitVec* connGraphPointees = *pConnGraphPointees;

    BuildConnGraphVisitorCallbackData callbackData(this, connGraphPointees);

    for (unsigned int lclNum = 0; lclNum < comp->lvaCount; ++lclNum)
    {
        var_types type = comp->lvaTable[lclNum].TypeGet();

        if (type == TYP_REF || type == TYP_I_IMPL || type == TYP_BYREF)
        {
            // A local variable of TYP_REF can potentially points to other local variables
            // For such variable we maintain bit-set of pointees
            connGraphPointees[lclNum] = BitVecOps::MakeEmpty(&m_bitVecTraits);

            if (comp->lvaTable[lclNum].lvAddrExposed)
            {
                JITDUMP("   V%02u is address exposed\n", lclNum);
                callbackData.MarkLclVarAsNonStackAlloc(lclNum);
            }
        }
        else
        {
            // Other local variable will not participate in our analysis
            connGraphPointees[lclNum] = BitVecOps::UninitVal();
        }
    }

    BasicBlock* block;

    foreach_block(comp, block)
    {
        for (GenTreeStmt* stmt = block->firstStmt(); stmt; stmt = stmt->gtNextStmt)
        {
            const bool lclVarsOnly  = false;
            const bool computeStack = true;

            comp->fgWalkTreePre(&stmt->gtStmtExpr, BuildConnGraphVisitor, &callbackData, lclVarsOnly, computeStack);
        }
    }
}

void ObjectAllocator::ComputeReachableNodes(BitVecTraits* bitVecTraits, BitVec* adjacentNodes, BitVec& reachableNodes)
{
    BitSetShortLongRep pointers = BitVecOps::MakeCopy(bitVecTraits, reachableNodes);
    BitSetShortLongRep pointees = BitVecOps::UninitVal();

    unsigned int lclNum;

    bool doOneMoreIteration = true;
    while (doOneMoreIteration)
    {
        BitVecOps::Iter iterator(bitVecTraits, pointers);
        doOneMoreIteration = false;

        while (iterator.NextElem(&lclNum))
        {
            doOneMoreIteration = true;

            // pointees         = adjacentNodes[lclNum]
            BitVecOps::Assign(bitVecTraits, pointees, adjacentNodes[lclNum]);
            // pointees         = pointees \ reachableLclVars
            BitVecOps::DiffD(bitVecTraits, pointees, reachableNodes);
            // pointers         = pointers U pointees
            BitVecOps::UnionD(bitVecTraits, pointers, pointees);
            // reachableLclVars = reachableLclVars U pointees
            BitVecOps::UnionD(bitVecTraits, reachableNodes, pointees);
            // pointers         = pointers \ { lclNum }
            BitVecOps::RemoveElemD(bitVecTraits, pointers, lclNum);
        }
    }
}

//------------------------------------------------------------------------
// MorphAllocObjNodes: Morph each GT_ALLOCOBJ node either into an
//                     allocation helper call or stack allocation.
//
// Returns:
//    true if any allocation was done as a stack allocation.
//
// Notes:
//    Runs only over the blocks having bbFlags BBF_HAS_NEWOBJ set.

bool ObjectAllocator::MorphAllocObjNodes()
{
    TarjanStronglyConnectedComponents tarjanScc(comp);

    if (IsObjectStackAllocationEnabled())
    {
        tarjanScc.DoAnalysis();
    }

    bool didStackAllocate = false;

    BasicBlock* block;

    foreach_block(comp, block)
    {
        const bool basicBlockHasNewObj = (block->bbFlags & BBF_HAS_NEWOBJ) == BBF_HAS_NEWOBJ;
#ifndef DEBUG
        if (!basicBlockHasNewObj)
        {
            continue;
        }
#endif // DEBUG

        for (GenTreeStmt* stmt = block->firstStmt(); stmt; stmt = stmt->gtNextStmt)
        {
            GenTree* stmtExpr = stmt->gtStmtExpr;
            GenTree* op2      = nullptr;

            bool canonicalAllocObjFound = false;

            if (stmtExpr->OperGet() == GT_ASG && stmtExpr->TypeGet() == TYP_REF)
            {
                op2 = stmtExpr->gtGetOp2();

                if (op2->OperGet() == GT_ALLOCOBJ)
                {
                    canonicalAllocObjFound = true;
                }
            }

            if (canonicalAllocObjFound)
            {
                assert(basicBlockHasNewObj);
                //------------------------------------------------------------------------
                // We expect the following expression tree at this point
                //  *  GT_STMT   void  (top level)
                // 	|  /--*  GT_ALLOCOBJ   ref
                // 	\--*  GT_ASG    ref
                // 	   \--*  GT_LCL_VAR    ref
                //------------------------------------------------------------------------

                GenTree* op1 = stmtExpr->gtGetOp1();

                assert(op1->OperGet() == GT_LCL_VAR);
                assert(op1->TypeGet() == TYP_REF);
                assert(op2 != nullptr);
                assert(op2->OperGet() == GT_ALLOCOBJ);

                GenTreeAllocObj*     asAllocObj = op2->AsAllocObj();
                unsigned int         lclNum     = op1->AsLclVar()->GetLclNum();
                CORINFO_CLASS_HANDLE clsHnd     = op2->AsAllocObj()->gtAllocObjClsHnd;

                if (IsObjectStackAllocationEnabled() && CanAllocateLclVarOnStack(lclNum, clsHnd) &&
                    !tarjanScc.IsPartOfCycle(block->bbNum))
                {
                    JITDUMP("Allocating local variable V%02u on the stack\n", lclNum);

                    const unsigned int stackLclNum = MorphAllocObjNodeIntoStackAlloc(asAllocObj, block, stmt);
                    m_HeapLocalToStackLocalMap.AddOrUpdate(lclNum, stackLclNum);
                    stmt->gtStmtExpr->gtBashToNOP();
                    comp->optMethodFlags |= OMF_HAS_OBJSTACKALLOC;
                    didStackAllocate = true;
                }
                else
                {
                    if (IsObjectStackAllocationEnabled())
                    {
                        JITDUMP("Allocating local variable V%02u on the heap\n", lclNum);
                    }

                    op2 = MorphAllocObjNodeIntoHelperCall(asAllocObj);
                }

                if (IsRunningAfterMorph())
                {
                    comp->fgMorphBlockStmt(block, stmt DEBUGARG("MorphAllocObjNodes"));
                }

                // Propagate flags of op2 to its parent.
                stmtExpr->gtOp.gtOp2 = op2;
                stmtExpr->gtFlags |= op2->gtFlags & GTF_ALL_EFFECT;
            }

#ifdef DEBUG
            else
            {
                // We assume that GT_ALLOCOBJ nodes are always present in the
                // canonical form.
                comp->fgWalkTreePre(&stmt->gtStmtExpr, AssertWhenAllocObjFoundVisitor);
            }
#endif // DEBUG
        }
    }

    return didStackAllocate;
}

//------------------------------------------------------------------------
// MorphAllocObjNodeIntoHelperCall: Morph a GT_ALLOCOBJ node into an
//                                  allocation helper call.
//
// Arguments:
//    allocObj - GT_ALLOCOBJ that will be replaced by helper call.
//
// Return Value:
//    Address of helper call node (can be the same as allocObj).
//
// Notes:
//    Must update parents flags after this.
GenTree* ObjectAllocator::MorphAllocObjNodeIntoHelperCall(GenTreeAllocObj* allocObj)
{
    assert(allocObj != nullptr);

    GenTree* op1 = allocObj->gtGetOp1();

    GenTree* helperCall =
        comp->fgMorphIntoHelperCall(allocObj, allocObj->gtNewHelper, comp->gtNewArgList(op1), IsRunningAfterMorph());

    return helperCall;
}

//------------------------------------------------------------------------
// MorphAllocObjNodeIntoStackAlloc: Morph a GT_ALLOCOBJ node into stack
//                                  allocation.
// Arguments:
//    allocObj - GT_ALLOCOBJ that will be replaced by helper call.
//    block    - a basic block where allocObj is
//    stmt     - a statement where allocObj is
//
// Return Value:
//    local num for the new stack allocated local
//
// Notes:
//    This function can insert additional statements before stmt.
unsigned int ObjectAllocator::MorphAllocObjNodeIntoStackAlloc(GenTreeAllocObj* allocObj,
                                                              BasicBlock*      block,
                                                              GenTreeStmt*     stmt)
{
    assert(allocObj != nullptr);
    assert(m_AnalysisDone);

    const unsigned int lclNum = comp->lvaGrabTemp(false DEBUGARG("MorphAllocObjNodeIntoStackAlloc temp"));
    comp->lvaSetStruct(lclNum, allocObj->gtAllocObjClsHnd, true);

// Because we've ruled out structs allocated within loops
// and we've zero inited in the prolog, we don't need to zero init here.

#if 0

    unsigned int structSize = comp->lvaTable[lclNum].lvSize();

    //------------------------------------------------------------------------
    // *  GT_STMT  void  (top level)
    // |  /--*  GT_CNS_INT
    // \--*  GT_BLK   void
    //    |  /--*  GT_CNS_INT     int    0
    //    \--*  <list>    void
    //       \--*  GT_ADDR      byref
    //          \--*  GT_LCL_VAR    struct(AX)
    //------------------------------------------------------------------------

    tree = comp->gtNewLclvNode(lclNum, TYP_STRUCT);
    tree = comp->gtNewBlkOpNode(tree, comp->gtNewIconNode(0), structSize, false, false);

    GenTreeStmt* newStmt = comp->gtNewStmt(tree);

    comp->fgInsertStmtBefore(block, stmt, newStmt);
    if (IsRunningAfterMorph())
    {
        comp->fgMorphBlockStmt(block, newStmt DEBUGARG("MorphAllocObjNodeIntoStackAlloc"));
    }

#endif

    //------------------------------------------------------------------------
    // *  GT_STMT   void
    // |  /--*  GT_CNS_INT  long
    // \--*  GT_ASG    long
    //    \--*  GT_FIELD     long  [+0]
    //          \--*  GT_ADDR
    //              \--*  GT_LCL_VAR  lclNum
    //------------------------------------------------------------------------

    const unsigned objHeaderSize = 0; // comp->info.compCompHnd->getObjHeaderSize();

    GenTree* tree = comp->gtNewLclvNode(lclNum, TYP_STRUCT);
    tree          = comp->gtNewOperNode(GT_ADDR, TYP_BYREF, tree);
    tree          = comp->gtNewFieldRef(TYP_I_IMPL, FieldSeqStore::FirstElemPseudoField, tree, objHeaderSize);
    tree          = comp->gtNewAssignNode(tree, allocObj->gtGetOp1());

    GenTreeStmt* newStmt = comp->gtNewStmt(tree);

    comp->fgInsertStmtBefore(block, stmt, newStmt);

    if (IsRunningAfterMorph())
    {
        comp->fgMorphBlockStmt(block, newStmt DEBUGARG("MorphAllocObjNodeIntoStackAlloc"));
    }

    return lclNum;
}

Compiler::fgWalkResult ObjectAllocator::BuildConnGraphVisitor(GenTree** pTree, Compiler::fgWalkData* data)
{
    GenTree* tree = *pTree;
    assert(tree);

    if (tree->OperGet() == GT_LCL_VAR && (tree->TypeGet() == TYP_REF || tree->TypeGet() == TYP_I_IMPL))
    {
        Compiler*                          compiler = data->compiler;
        GenTree*                           parent   = data->parent;
        BuildConnGraphVisitorCallbackData* callbackData =
            reinterpret_cast<BuildConnGraphVisitorCallbackData*>(data->pCallbackData);

        unsigned int lclNum = tree->AsLclVar()->GetLclNum();

        if (parent->OperGet() == GT_ASG)
        {
            GenTree* op1 = parent->AsOp()->gtGetOp1();

            // We don't do any analysis when lclVar on the lhs of GT_ASG node
            // If there is another local variable on the rhs, eventually, we
            // will be there. Otherwise, we can ignore this assignment.
            if (op1 != tree)
            {
                // lclVar is on the rhs of GT_ASG node
                assert(parent->AsOp()->gtGetOp2() == tree);

                if (op1->OperGet() == GT_LCL_VAR)
                {
                    //------------------------------------------------------------------------
                    // We expect the following tree at this point
                    //   /--*  GT_LCL_VAR    ref    pointeeLclVar
                    // --*  =         ref
                    //   \--*  GT_LCL_VAR    ref    pointerLclVar
                    //------------------------------------------------------------------------
                    const unsigned int pointerLclNum = op1->AsLclVar()->GetLclNum();
                    const unsigned int pointeeLclNum = lclNum;

                    callbackData->SetPointerPointeeRel(pointerLclNum, pointeeLclNum);
                }
                else
                {
                    //------------------------------------------------------------------------
                    // Use the following conservative behaviour for GT_ASG parent node:
                    //   Do not allow local variable (TYP_REF) to be allocated on the stack if
                    //   1. lclVar appears on the rhs of a GT_ASG node
                    //                      AND
                    //   2. The lhs of the GT_ASG is not another lclVar
                    //------------------------------------------------------------------------
                    if (!callbackData->IsLclVarNonStackAlloc(lclNum))
                    {
                        JITDUMP("V%02u first escapes (1) via [%06u]\n", lclNum, tree->gtTreeID);
                    }
                    callbackData->MarkLclVarAsNonStackAlloc(lclNum);
                }
            }
        }
        else if (parent->OperGet() == GT_ADD)
        {
            parent = data->parentStack->Index(2);

            if (parent->OperGet() == GT_ASG)
            {
                GenTree* op1 = parent->AsOp()->gtGetOp1();

                if (op1->OperGet() == GT_LCL_VAR)
                {
                    const unsigned int pointerLclNum = op1->AsLclVar()->GetLclNum();
                    const unsigned int pointeeLclNum = lclNum;

                    callbackData->SetPointerPointeeRel(pointerLclNum, pointeeLclNum);
                }
            }
            else if (CanLclVarEscapeViaParentStack(data->parentStack, data->compiler, lclNum))
            {
                if (!callbackData->IsLclVarNonStackAlloc(lclNum))
                {
                    JITDUMP("V%02u first escapes (2) via [%06u]\n", lclNum, tree->gtTreeID);
                }
                callbackData->MarkLclVarAsNonStackAlloc(lclNum);
            }
        }
        else if (CanLclVarEscapeViaParentStack(data->parentStack, data->compiler, lclNum))
        {
            if (!callbackData->IsLclVarNonStackAlloc(lclNum))
            {
                JITDUMP("V%02u first escapes (3) via [%06u]\n", lclNum, tree->gtTreeID);
            }
            callbackData->MarkLclVarAsNonStackAlloc(lclNum);
        }
    }
    return Compiler::fgWalkResult::WALK_CONTINUE;
}

//------------------------------------------------------------------------
// CanLclVarEscapeViaParentStack: TODO
//
// Arguments:
//    parentStack -
//    compiler    -
bool ObjectAllocator::CanLclVarEscapeViaParentStack(ArrayStack<GenTree*>* parentStack,
                                                    Compiler*             compiler,
                                                    unsigned int          lclNum)
{
    assert(parentStack);

    //--------------------------------------------------------------------------
    // We consider only the following simplest scenarios for now:
    //   1. When node.parent is any of the following nodes: GT_IND, GT_EQ, GT_NE
    //   2. When node.parent is GT_ADD and node.parent.parent is GT_IND
    //   3. When node.parent is GT_CALL to a pure helper
    //   5. When node.parent is GT_FIELD on RHS of a GT_ASG
    //--------------------------------------------------------------------------

    bool canLclVarEscapeViaParentStack = true;

    if (parentStack->Height() > 1)
    {
        GenTree* parent = parentStack->Index(1);

        switch (parent->OperGet())
        {
            case GT_EQ:
            case GT_NE:
            case GT_IND:
                canLclVarEscapeViaParentStack = false; // Scenario (1)
                break;

            case GT_FIELD:
                if (parentStack->Height() > 2)
                {
                    // TODO: This is not sufficient, we need to keep walking
                    // up looking for a GT_ADDR not covered by a GT_IND.
                    GenTree* grandParent          = parentStack->Index(2);
                    canLclVarEscapeViaParentStack = (grandParent->OperGet() == GT_ADDR); // Scenario (5)
                }
                break;

            case GT_ADD:
                if (parentStack->Height() > 2)
                {
                    GenTree* grandParent = parentStack->Index(2);

                    switch (grandParent->OperGet())
                    {
                        case GT_IND:
                            canLclVarEscapeViaParentStack = false; // Scenario (2)
                            break;
                    }
                }
                break;

            case GT_CALL:
            {
                GenTreeCall* asCall = parent->AsCall();

                if (asCall->gtCallType == CT_HELPER)
                {
                    const CorInfoHelpFunc helperNum = compiler->eeGetHelperNum(asCall->gtCallMethHnd);

                    if (Compiler::s_helperCallProperties.IsPure(helperNum))
                    {
                        canLclVarEscapeViaParentStack = false; // Scenario (3)
                    }
                }
            }
            break;
        }
    }

    return canLclVarEscapeViaParentStack;
}

#ifdef DEBUG

//------------------------------------------------------------------------
// AssertWhenAllocObjFoundVisitor: Look for a GT_ALLOCOBJ node and assert
//                                 when found one.
//
// Arguments:
//    pTree -
//    data  -
Compiler::fgWalkResult ObjectAllocator::AssertWhenAllocObjFoundVisitor(GenTree** pTree, Compiler::fgWalkData* data)
{
    GenTree* tree = *pTree;

    assert(tree != nullptr);
    assert(tree->OperGet() != GT_ALLOCOBJ);

    return Compiler::fgWalkResult::WALK_CONTINUE;
}

#endif // DEBUG

//------------------------------------------------------------------------
// RewriteUses: find uses of the newobj temp for stack allocated
//    objects and replace with address of the stack local + offset.

void ObjectAllocator::RewriteUses()
{
    BasicBlock* block;

    foreach_block(comp, block)
    {
        for (GenTreeStmt* stmt = block->firstStmt(); stmt; stmt = stmt->gtNextStmt)
        {
            const bool lclVarsOnly  = true;
            const bool computeStack = false;

            comp->fgWalkTreePre(&stmt->gtStmtExpr, RewriteUsesVisitor, this, lclVarsOnly, computeStack);
        }
    }
}

//------------------------------------------------------------------------
// RewriteUses: find uses of the newobj temp for stack allocated
//    objects and replace with address of the stack local

Compiler::fgWalkResult ObjectAllocator::RewriteUsesVisitor(GenTree** pTree, Compiler::fgWalkData* data)
{
    ObjectAllocator* allocator = reinterpret_cast<ObjectAllocator*>(data->pCallbackData);
    GenTree*         tree      = *pTree;
    assert(tree != nullptr);
    assert(tree->IsLocal());

    const unsigned int lclNum    = tree->AsLclVarCommon()->gtLclNum;
    unsigned int       newLclNum = BAD_VAR_NUM;
    Compiler*          comp      = allocator->comp;

    if (allocator->m_HeapLocalToStackLocalMap.TryGetValue(lclNum, &newLclNum))
    {
        GenTree* newTree = comp->gtNewOperNode(GT_ADDR, TYP_BYREF, comp->gtNewLclvNode(newLclNum, TYP_STRUCT));
        *pTree           = newTree;
    }

    return Compiler::fgWalkResult::WALK_CONTINUE;
}
